using System.Collections.Concurrent;
using System.Diagnostics;
using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Mind;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Governor;
using Emberloom.Mind.Ollama;
using Emberloom.Core.Memory;
using Emberloom.Core.Events;
using Emberloom.Core.Simulation;
using Emberloom.Core.World;
using Emberloom.Core.Forks;
using Emberloom.Core.Verbs;
using Emberloom.Core.Culture;
using Emberloom.Core.Chronicle;

namespace Emberloom.Api;

/// <summary>
/// Owns the one running colony and the clock that drives it. The simulation runs on its own loop
/// at real-time speed rather than as fast as it can, because from M2 the governor's ceiling is
/// measured in real-world tokens per minute — game time has to be pinned to wall time for that
/// arithmetic to mean anything.
/// </summary>
public sealed class SimulationHost : BackgroundService
{
    /// <summary>How often the loop wakes. Fine enough to feel live, coarse enough to be cheap.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly ILogger<SimulationHost> _logger;
    private readonly ConcurrentDictionary<Guid, Channel> _subscribers = new();
    private readonly object _gate = new();

    private Colony _colony;
    private BodySimulation _body;
    private Random _random;

    private readonly MindService? _mind;
    private readonly MindGovernor? _governor;

    /// <summary>
    /// What the player is looking at: the room open in the main view, and the colonist in the
    /// inspector. A conversation there is written before one nobody is watching.
    /// </summary>
    private sealed record ViewFocus(string? PlaceId, string? ColonistId);

    private volatile ViewFocus _view = new(null, null);

    /// <summary>When each pair last had words written for them, in colony days. See SubmitDialogue.</summary>
    private readonly Dictionary<string, double> _lastDialogue = [];

    public SimulationHost(ILogger<SimulationHost> logger, IConfiguration configuration)
    {
        _logger = logger;

        var seed = configuration.GetValue("Simulation:Seed", 4);
        var colonists = configuration.GetValue("Simulation:Colonists", 8);

        if (configuration.GetValue("Mind:Enabled", true))
        {
            // The endpoint is a setting with a localhost default on purpose. Local play and a
            // remote inference box are the same code path — which is only true because every mind
            // call carries its whole context and the thing serving it holds no colony state.
            _mind = new MindService(new OllamaOptions
            {
                Endpoint = new Uri(configuration.GetValue("Mind:Endpoint", "http://127.0.0.1:11434")!),
                Deep = new LaneConfig
                {
                    Model = configuration.GetValue("Mind:DeepModel", "llama3.2:3b")!,
                    NumCtx = configuration.GetValue("Mind:DeepContext", 4096),
                    KeepAlive = "60m"
                },
                Fast = new LaneConfig
                {
                    Model = configuration.GetValue("Mind:FastModel", "llama3.2:1b")!,
                    NumCtx = configuration.GetValue("Mind:FastContext", 2048),
                    KeepAlive = "60m"
                }
            })
            {
                // Everything the mind writes back lands under the same lock the tick loop and the
                // API read under. The inference itself stays outside it — holding the colony across
                // a multi-second call would stall the simulation, which Layer 2 is never allowed to do.
                Commit = work =>
                {
                    lock (_gate)
                    {
                        work();
                    }
                }
            };

            // The ceiling is real-world tokens per minute, for the machine actually serving. It has
            // to sit ABOVE measured sustained throughput: it exists to stop demand outrunning
            // supply, not to hold the machine below its own speed. Set to 2400 initially, which
            // was under the ~2650 tok/min this laptop actually sustains, and the result was a
            // governor that throttled permanently and pinned the simulation at 1x.
            _governor = new MindGovernor(
                tokensPerMinute: configuration.GetValue("Mind:TokensPerMinute", 3600),
                maxQueueDepth: configuration.GetValue("Mind:MaxQueueDepth", 32),
                perColonistCooldown: TimeSpan.FromSeconds(
                    configuration.GetValue("Mind:PerColonistCooldownSeconds", 20)));

            // The one place time bends for function: an undroppable call that cannot be served
            // inside the budget slows the simulation rather than being skipped.
            _governor.ThrottleRequested += throttling =>
            {
                if (throttling)
                {
                    Clock.ThrottleForThought();
                    _logger.LogInformation("Slowed the simulation: a turning point could not be served at speed.");
                }
                else
                {
                    Clock.ReleaseThrottle();
                }
            };
        }

        // The M0 benchmark figure for this machine, and only a starting point: once a few deep
        // calls have been served, the Contemplation readout uses what they actually took. At M4 the
        // laptop's live reflections came in at about a quarter of this figure.
        _benchmarkCallsPerMinute = configuration.GetValue("Simulation:SustainedCallsPerMinute", 4.8);

        // Where the colony's story lives, so it can be read and rewritten by hand.
        _storyPath = Path.GetFullPath(configuration.GetValue("Myth:StoryFile", "story.md")!);

        // And where the account of this session goes. Nothing in the colony survives the process,
        // so this file is the only thing a session leaves behind.
        _chronicleFolder = Path.GetFullPath(configuration.GetValue("Chronicle:Folder", "chronicles")!);
        _chronicleEnabled = configuration.GetValue("Chronicle:Enabled", true);

        _random = new Random(seed);
        _colony = ColonyFactory.Create(colonists, _random);
        _body = new BodySimulation(_colony, _random);
        AttachBody();

        _colony.Feed.Appended += OnEvent;
        _chronicle = new SessionChronicle(seed, colonists, DateTimeOffset.Now);
        QueueKindlingForEveryone();
    }

    /// <summary>
    /// Created with the host rather than partway through its constructor: the governor's throttle
    /// handler, wired up earlier in the constructor, closes over it.
    /// </summary>
    public SimulationClock Clock { get; } = new();

    public bool MindConfigured => _mind is not null;

    private readonly double _benchmarkCallsPerMinute;
    private readonly ThroughputMeter _deepCalls = new();

    /// <summary>
    /// Deep calls this machine serves per minute: measured from the calls actually served, or the
    /// benchmark figure until there are enough of them to say. The benchmark's call shapes were
    /// smaller than a live reflection batch, so trusting it made the Contemplation readout about
    /// four times too generous on the laptop.
    /// </summary>
    public double SustainedCallsPerMinute => _deepCalls.CallsPerMinute ?? _benchmarkCallsPerMinute;

    public bool ThroughputMeasured => _deepCalls.CallsPerMinute is not null;

    /// <summary>
    /// Everything that reads colony state must hold this. The tick loop mutates it from a
    /// background thread while HTTP requests and socket pushes read it.
    /// </summary>
    public T Read<T>(Func<Colony, BodySimulation, T> read)
    {
        lock (_gate)
        {
            return read(_colony, _body);
        }
    }

    public void Restart(int colonists, int seed)
    {
        // The colony that is about to be thrown away gets its last page first. A new colony is a
        // new session and a new file; nothing about this one is carried into it.
        WriteChronicle(ended: true);

        lock (_gate)
        {
            _colony.Feed.Appended -= OnEvent;

            _random = new Random(seed);
            _colony = ColonyFactory.Create(colonists, _random);
            _body = new BodySimulation(_colony, _random);
            AttachBody();
            _lastDialogue.Clear();
            _lastMythAttempt = double.NegativeInfinity;

            // A new colony reads the story file again: a story left there by hand is one it can
            // begin with, rather than something the restart quietly threw away.
            _storyWrittenAt = default;
            _storyOnDisk = string.Empty;
            _colony.Feed.Appended += OnEvent;

            _chronicle = new SessionChronicle(seed, colonists, DateTimeOffset.Now);
            _chronicleWrittenAt = DateTimeOffset.MinValue;
        }

        QueueKindlingForEveryone();
        _logger.LogInformation("Colony restarted with {Colonists} colonists, seed {Seed}.", colonists, seed);
    }

    private void QueueKindlingForEveryone()
    {
        foreach (var colonist in _colony.Colonists.Where(c => !c.IsKindled))
        {
            SubmitKindling(colonist);
        }
    }

    /// <summary>
    /// Kindling is undroppable. A colonist who was never written would act on archetype weights
    /// forever, which is the one degradation the design refuses to accept quietly.
    /// </summary>
    private void SubmitKindling(Colonist colonist, string? provenance = null, bool born = false)
    {
        if (_mind is null || _governor is null)
        {
            return;
        }

        var submitted = _governor.Submit(new MindRequest
        {
            Call = CallType.Kindling,
            Priority = CallPriority.Kindling,
            ColonistId = colonist.Id,
            Undroppable = true,
            Work = async ct =>
            {
                try
                {
                    // One path for Kindling, shared with the tests: MindService makes the call
                    // outside the colony lock, commits the result under it, and falls back — with
                    // a trace line saying so — when the mind is not there. This used to be a second
                    // copy of that logic which threw on an unreachable mind instead; the governor
                    // treats a throw as lost work, so a colonist kindled while Ollama was down got
                    // no trace line at all.
                    var colony = Read((c, _) => c);
                    var started = Stopwatch.GetTimestamp();
                    var result = await _mind.KindleAsync(colonist, colony, colonist.Seed, provenance, ct, born).ConfigureAwait(false);

                    // Only a call that reached a model counts toward throughput. An unreachable
                    // mind fails in a moment, and counting that would make the machine look fast.
                    if (result.Call is not null)
                    {
                        _deepCalls.Record(Stopwatch.GetElapsedTime(started));
                    }

                    return new MindCallOutcome
                    {
                        PromptTokens = result.Call?.PromptTokens ?? 0,
                        CompletionTokens = result.Call?.CompletionTokens ?? 0,
                        WasFallback = !result.FromModel
                    };
                }
                finally
                {
                    // Done waiting whatever happened, or a failed call would hold every fork off.
                    Read((_, _) => colonist.AwaitingKindling = false);
                    Broadcast(Snapshot());
                }
            }
        });

        if (submitted)
        {
            colonist.AwaitingKindling = true;
        }
    }

    /// <summary>
    /// Offers work to the governor. It decides what actually runs; this only decides what is worth
    /// asking for, and asks often enough that the queue always has something to serve.
    /// <para>
    /// Reflection carries its accumulated salience as weight, so the colonist holding the most
    /// unprocessed experience is served first. Idle thought is offered only to colonists with
    /// nothing to reflect on, and is refused freely — it writes nothing durable, so a dropped one
    /// costs nothing. That refusal is the mechanism behind the Contemplation readout: at speed the
    /// cheap calls stop being served long before the important ones do.
    /// </para>
    /// </summary>
    private async Task ScheduleThinkingAsync(CancellationToken stoppingToken)
    {
        if (_mind is null || _governor is null)
        {
            return;
        }

        var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // The colony's story is rare and colony-wide, so it is offered whatever the depth; the
            // governor places it by priority. Everything below waits for room.
            if (!_mythInFlight
                && Read((colony, _) => CultureRules.MythDue(colony)
                                       && colony.Day - _lastMythAttempt >= CultureRules.DaysBetweenTellings / 2))
            {
                SubmitMyth();
            }

            var room = TargetQueueDepth - _governor.Stats().QueueDepth;
            if (room <= 0)
            {
                continue;
            }

            var candidates = Read((colony, _) => colony.Living
                .Select(c => (Colonist: c, Salience: c.Memory.UnconsolidatedSalience(colony.Day)))
                .OrderByDescending(x => x.Salience)
                .Take(room)
                .ToList());

            foreach (var (colonist, salience) in candidates)
            {
                if (salience >= MemoryStore.ReflectionThreshold)
                {
                    SubmitReflection(colonist, salience);
                }
                else
                {
                    SubmitIdleThought(colonist);
                }
            }
        }
    }

    private void SubmitReflection(Colonist colonist, double salience)
    {
        _governor!.Submit(new MindRequest
        {
            Call = CallType.Reflection,
            Priority = CallPriority.Reflection,
            ColonistId = colonist.Id,
            Weight = salience,
            Work = async ct =>
            {
                var colony = Read((c, _) => c);
                var started = Stopwatch.GetTimestamp();
                var result = await _mind!.ReflectAsync(colonist, colony, ct).ConfigureAwait(false);

                // Wall time for the whole reflection, embeddings included: that is how long it
                // occupies the mind, which is what the readout is asking.
                if (result.Call is not null)
                {
                    _deepCalls.Record(Stopwatch.GetElapsedTime(started));
                }

                Broadcast(Snapshot());

                return new MindCallOutcome
                {
                    PromptTokens = result.Call?.PromptTokens ?? 0,
                    CompletionTokens = result.Call?.CompletionTokens ?? 0,
                    WasFallback = !result.FromModel
                };
            }
        });
    }

    private void AttachBody()
    {
        _body.ConversationStarted += SubmitDialogue;
        _body.ForkOpened += OnForkOpened;
        _body.ForkResolved += OnForkResolved;
        _body.ColonistBorn += OnBorn;
        _body.Coinable += SubmitCoinage;
    }

    /// <summary>A word for something found, or for a practice. Droppable: a word can wait, or go unsaid.</summary>
    private void SubmitCoinage(Occasion occasion)
    {
        if (_mind is null || _governor is null || _colony.Find(occasion.ColonistId) is not { } coiner)
        {
            return;
        }

        _governor.Submit(new MindRequest
        {
            Call = CallType.Coinage,
            Priority = CallPriority.Coinage,
            ColonistId = coiner.Id,
            Work = async ct =>
            {
                var colony = Read((c, _) => c);
                var started = Stopwatch.GetTimestamp();
                var result = await _mind.CoinAsync(coiner, colony, occasion, ct).ConfigureAwait(false);

                if (result.Call is not null)
                {
                    _deepCalls.Record(Stopwatch.GetElapsedTime(started));
                }

                Broadcast(Snapshot());

                return new MindCallOutcome
                {
                    PromptTokens = result.Call?.PromptTokens ?? 0,
                    CompletionTokens = result.Call?.CompletionTokens ?? 0,
                    WasFallback = !result.FromModel
                };
            }
        });
    }

    /// <summary>
    /// How deep the scheduler keeps the queue. It is a work buffer, not a firehose: offering
    /// everything every tick would refuse hundreds of requests a minute and make the drop count
    /// meaningless — a drop should mean "something worth doing lost to something worth more", not
    /// "the scheduler shouted again". Off-screen dialogue respects it too; see SubmitDialogue.
    /// </summary>
    private const int TargetQueueDepth = 6;

    private volatile bool _mythInFlight;
    private double _lastMythAttempt = double.NegativeInfinity;

    // ---- the account of this session, on disk -------------------------------------------------

    private readonly string _chronicleFolder;
    private readonly bool _chronicleEnabled;
    private SessionChronicle _chronicle;
    private DateTimeOffset _chronicleWrittenAt = DateTimeOffset.MinValue;

    /// <summary>Where this session's record is being written, for anyone who wants to go and read it.</summary>
    public string ChroniclePath => Path.Combine(_chronicleFolder, _chronicle.FileName);

    /// <summary>The record as it stands, for anyone who would rather read it here than on disk.</summary>
    public string ComposeChronicle() => Compose(ended: false).Text;

    /// <summary>
    /// The record and the chronicle that owns it. Composed under the colony lock because it reads
    /// the colony; writing it out happens outside, because a disk is slower than a tick.
    /// </summary>
    private (SessionChronicle Chronicle, string Text) Compose(bool ended)
    {
        var stamp = new ChronicleStamp(
            DateTimeOffset.Now,
            DateTimeOffset.Now - _chronicle.StartedAt,
            SimulationClock.Label(Clock.Speed),
            _governor?.Stats().Served ?? 0,
            _governor?.Stats().Dropped ?? 0,
            _governor?.Stats().Fallbacks ?? 0,
            ended,
            MindConfigured);

        lock (_gate)
        {
            return (_chronicle, _chronicle.Compose(_colony, stamp));
        }
    }

    /// <summary>Amends the file. Everything notable that has happened is in it once this returns.</summary>
    private void WriteChronicle(bool ended = false)
    {
        if (!_chronicleEnabled)
        {
            return;
        }

        var (chronicle, text) = Compose(ended);

        try
        {
            ChronicleFile.Save(_chronicleFolder, chronicle.FileName, text);
            chronicle.MarkWritten();
            _chronicleWrittenAt = DateTimeOffset.Now;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not write the colony's record to {Path}.", _chronicleFolder);
        }
    }

    /// <summary>
    /// Folds finished colony days into the record, and amends the file. Days are closed one at a
    /// time even when a frame covered several, so an Overnight run's record has every day in it.
    /// </summary>
    private void KeepTheRecord()
    {
        if (!_chronicleEnabled)
        {
            return;
        }

        var today = Read((colony, _) => (int)colony.Day);
        var closed = false;

        while (_chronicle.LastDayClosed < today)
        {
            var day = _chronicle.LastDayClosed + 1;
            lock (_gate)
            {
                _chronicle.CloseDay(_colony, day);
            }

            closed = true;
        }

        // Every colony day, and in between whenever something notable has happened and the last
        // amendment is old enough — a colony day is a real hour at 1×, and this file exists for the
        // stop nobody planned.
        if (closed
            || (_chronicle.HasNews && DateTimeOffset.Now - _chronicleWrittenAt >= ChronicleRules.AtMostEvery))
        {
            WriteChronicle();
        }
    }

    // ---- the story, on disk ------------------------------------------------------------------

    private readonly string _storyPath;
    private string _storyOnDisk = string.Empty;
    private DateTime _storyWrittenAt;

    /// <summary>
    /// Writes the colony's story out after every telling, so there is always a file to read — and
    /// to rewrite. What is written is what the colony is telling now.
    /// </summary>
    private void WriteStory()
    {
        try
        {
            var text = Read((colony, _) => MythFile.Compose(colony));
            File.WriteAllText(_storyPath, text);
            _storyOnDisk = text;
            _storyWrittenAt = File.GetLastWriteTimeUtc(_storyPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not write the colony's story to {Path}.", _storyPath);
        }
    }

    /// <summary>
    /// Takes up a story rewritten by hand. Checked a couple of times a second against the file's
    /// timestamp, which costs nothing; the colony's own writes are recognised by their contents and
    /// ignored, so writing the file back cannot loop. A story seeded before the colony starts is
    /// picked up the same way, which is how a new colony can begin with one you wrote.
    /// </summary>
    private void ReadStoryIfChanged()
    {
        try
        {
            if (!File.Exists(_storyPath))
            {
                WriteStory();
                return;
            }

            var writtenAt = File.GetLastWriteTimeUtc(_storyPath);
            if (writtenAt == _storyWrittenAt)
            {
                return;
            }

            _storyWrittenAt = writtenAt;

            var text = File.ReadAllText(_storyPath);
            if (text == _storyOnDisk)
            {
                return;
            }

            _storyOnDisk = text;
            var taken = false;
            var story = MythFile.ReadStory(text);

            lock (_gate)
            {
                taken = CultureRules.TellByHand(_colony, story);
            }

            if (taken)
            {
                _logger.LogInformation("The colony's story was rewritten by hand, in {Path}.", _storyPath);
                Broadcast(Snapshot());
                WriteStory();
            }
        }
        catch (IOException)
        {
            // Half-written, most likely: an editor saving. The next pass will read it.
        }
    }

    /// <summary>
    /// The colony's story of its makers, told or retold. One at a time, and not retried the moment
    /// a telling fails — an unreachable mind would otherwise be asked again every two seconds.
    /// </summary>
    private void SubmitMyth()
    {
        _lastMythAttempt = Read((colony, _) => colony.Day);

        var submitted = _governor!.Submit(new MindRequest
        {
            Call = CallType.Myth,
            Priority = CallPriority.Reflection,
            ColonistId = "colony",
            Weight = 10,
            Work = async ct =>
            {
                try
                {
                    var colony = Read((c, _) => c);
                    var started = Stopwatch.GetTimestamp();
                    var result = await _mind!.TellMythAsync(colony, ct).ConfigureAwait(false);

                    if (result.Call is not null)
                    {
                        _deepCalls.Record(Stopwatch.GetElapsedTime(started));
                    }

                    if (result.FromModel)
                    {
                        WriteStory();
                    }

                    Broadcast(Snapshot());

                    return new MindCallOutcome
                    {
                        PromptTokens = result.Call?.PromptTokens ?? 0,
                        CompletionTokens = result.Call?.CompletionTokens ?? 0,
                        WasFallback = !result.FromModel
                    };
                }
                finally
                {
                    _mythInFlight = false;
                }
            }
        });

        _mythInFlight = submitted;
    }

    /// <summary>
    /// A child. The body has made it, with its parents' crossed weights and their dreams; Kindling
    /// writes who it is around those weights and keeps them. Undroppable, like every Kindling.
    /// </summary>
    private void OnBorn(Colonist child, Colonist left, Colonist right) =>
        SubmitKindling(child,
            $"Born to {left.DisplayName} and {right.DisplayName}, not printed. Its weights are already theirs, crossed; write who it is around them.",
            born: true);

    /// <summary>
    /// A fork has opened: drop to 1x if the player asked for that, and ask each council member's
    /// mind where they stand. Deliberation is undroppable — if the governor cannot serve it at this
    /// speed, the simulation slows itself until it can. Called from inside the tick, under the lock.
    /// </summary>
    private void OnForkOpened(Fork fork)
    {
        Clock.SlowForEvent();

        // With no mind configured there is nobody to ask, and the body decides on where everyone
        // stands once the fork times out.
        if (_mind is null || _governor is null)
        {
            return;
        }

        for (var seat = 0; seat < fork.Participants.Count; seat++)
        {
            if (_colony.Find(fork.Participants[seat]) is { } member)
            {
                // Asked in council order: the first seat is served first, so each later member
                // hears what the earlier ones said.
                SubmitDeliberation(member, fork, weight: fork.Participants.Count - seat);
            }
        }
    }

    private void SubmitDeliberation(Colonist member, Fork fork, double weight)
    {
        var submitted = _governor!.Submit(new MindRequest
        {
            Call = CallType.Deliberation,
            Priority = CallPriority.Deliberation,
            ColonistId = member.Id,
            Undroppable = true,
            Weight = weight,
            Work = async ct =>
            {
                try
                {
                    var colony = Read((c, _) => c);
                    var started = Stopwatch.GetTimestamp();
                    var result = await _mind!.DeliberateAsync(member, colony, fork, ct).ConfigureAwait(false);

                    if (result.Call is not null)
                    {
                        _deepCalls.Record(Stopwatch.GetElapsedTime(started));
                    }

                    return new MindCallOutcome
                    {
                        PromptTokens = result.Call?.PromptTokens ?? 0,
                        CompletionTokens = result.Call?.CompletionTokens ?? 0,
                        WasFallback = !result.FromModel
                    };
                }
                finally
                {
                    // Whatever happened, this member is no longer being waited on. The fork only
                    // times out when nobody is — see Fork.AwaitingMinds.
                    Read((_, _) => --fork.AwaitingMinds);
                    Broadcast(Snapshot());
                }
            }
        });

        if (submitted)
        {
            fork.AwaitingMinds++;
        }
    }

    /// <summary>A newly printed mind needs writing. Kindling is undroppable, like the vote that made it.</summary>
    private void OnForkResolved(Fork fork, Colonist? printed)
    {
        if (printed is not null)
        {
            SubmitKindling(printed);
        }
    }

    public void SetView(string? placeId, string? colonistId) => _view = new ViewFocus(placeId, colonistId);

    // ---- the player's verbs (M6) ------------------------------------------------------------

    /// <summary>
    /// Speak. The words are remembered at once, under the lock, whether or not a mind ever answers;
    /// the answer is one call. It is submitted undroppable, like Kindling: the player asked a
    /// question and is waiting, and a governor that quietly dropped it — or held it behind the
    /// per-colonist cooldown — would look like the game ignoring them.
    /// </summary>
    public VerbResult Speak(string colonistId, string text)
    {
        VerbResult outcome;

        lock (_gate)
        {
            var colonist = _colony.Find(colonistId);
            if (colonist is null)
            {
                return VerbResult.Refused("There is nobody here by that name.");
            }

            outcome = PlayerVerbs.Speak(_colony, _body, colonist, text);
        }

        if (outcome.Ok && _mind is not null && _governor is not null)
        {
            var colonist = outcome.Colonist!;
            var words = text.Trim();
            var heardId = outcome.Memory?.Id;

            _governor.Submit(new MindRequest
            {
                Call = CallType.Dialogue,
                Priority = CallPriority.PlayerFocus,
                ColonistId = colonist.Id,
                Undroppable = true,
                Work = async ct =>
                {
                    var colony = Read((c, _) => c);
                    var started = Stopwatch.GetTimestamp();
                    var result = await _mind.AnswerAsync(colonist, colony, words, heardId, ct).ConfigureAwait(false);

                    if (result.Call is not null)
                    {
                        _deepCalls.Record(Stopwatch.GetElapsedTime(started));
                    }

                    Broadcast(Snapshot());

                    return new MindCallOutcome
                    {
                        PromptTokens = result.Call?.PromptTokens ?? 0,
                        CompletionTokens = result.Call?.CompletionTokens ?? 0,
                        WasFallback = !result.FromModel
                    };
                }
            });
        }

        return outcome;
    }

    /// <summary>Print. The new mind acts at once on an archetype; Kindling writes it from the seed.</summary>
    public VerbResult Print(string? seed)
    {
        VerbResult outcome;

        lock (_gate)
        {
            outcome = PlayerVerbs.Print(_colony, _body, _random, seed);
        }

        if (outcome.Ok)
        {
            SubmitKindling(outcome.Colonist!,
                string.IsNullOrWhiteSpace(seed)
                    ? null
                    : "Written by a voice with no body, which asked the Loom for this mind in these words.");
            Broadcast(Snapshot());
        }

        return outcome;
    }

    /// <summary>Provide: light, warmth, or a door. Body only — nothing here waits on a mind.</summary>
    public VerbResult Provide(string kind, string placeId)
    {
        if (!Enum.TryParse<ProvideKind>(kind, ignoreCase: true, out var what))
        {
            return VerbResult.Refused($"The Loom cannot provide '{kind}'.");
        }

        VerbResult outcome;
        lock (_gate)
        {
            outcome = PlayerVerbs.Provide(_colony, _body, what, placeId);
        }

        Broadcast(Snapshot());
        return outcome;
    }

    /// <summary>Withhold: cut light or warmth. Free, and they notice.</summary>
    public VerbResult Withhold(string kind, string placeId)
    {
        if (!Enum.TryParse<ProvisionKind>(kind, ignoreCase: true, out var what))
        {
            return VerbResult.Refused($"There is no '{kind}' to cut.");
        }

        VerbResult outcome;
        lock (_gate)
        {
            outcome = PlayerVerbs.Withhold(_colony, _body, what, placeId);
        }

        Broadcast(Snapshot());
        return outcome;
    }

    /// <summary>Reveal: show one colonist something of the makers'. Irreversible.</summary>
    public VerbResult Reveal(string revelationId, string colonistId)
    {
        VerbResult outcome;
        lock (_gate)
        {
            var colonist = _colony.Find(colonistId);
            if (colonist is null)
            {
                return VerbResult.Refused("There is nobody here by that name.");
            }

            outcome = PlayerVerbs.Reveal(_colony, _body, colonist, revelationId);
        }

        Broadcast(Snapshot());
        return outcome;
    }

    /// <summary>What there is to reveal, and who has been shown what already.</summary>
    public IReadOnlyList<RevelationDto> ListRevelations() => Read((colony, _) =>
        Revelations.All
            .Select(r => new RevelationDto(
                r.Id,
                r.Title,
                r.Unlocks,
                colony.Revealed.TryGetValue(r.Id, out var who) ? colony.Find(who)?.DisplayName ?? who : null))
            .ToList());

    /// <summary>
    /// Two colonists have started talking; ask the mind what they say. Called from inside the
    /// tick, under the colony lock.
    /// <para>
    /// One exchange per pair per half colony-day. Talk is the commonest thing colonists do, and a
    /// line for every conversation would spend the whole budget on small talk. A conversation the
    /// player is watching goes to the front of the queue — the design's first priority — and one
    /// nobody is watching waits behind reflection.
    /// </para>
    /// </summary>
    private void SubmitDialogue(Colonist speaker, Colonist listener, Place place)
    {
        if (_mind is null || _governor is null)
        {
            return;
        }

        var pair = string.CompareOrdinal(speaker.Id, listener.Id) < 0
            ? $"{speaker.Id}|{listener.Id}"
            : $"{listener.Id}|{speaker.Id}";

        if (_lastDialogue.TryGetValue(pair, out var last) && _colony.Day - last < 0.5)
        {
            return;
        }

        // While the colony is deciding, the council's say is its vote, not small talk. And watched
        // chatter loses its jump: the council sits at the plaza, so every conversation there counts
        // as watched, and live at M5 that queued them ahead of the fork's own deliberations.
        var fork = _colony.OpenFork;
        if (fork is not null && (fork.Participants.Contains(speaker.Id) || fork.Participants.Contains(listener.Id)))
        {
            return;
        }

        var view = _view;
        var watched = fork is null
                      && (view.ColonistId == speaker.Id
                          || view.ColonistId == listener.Id
                          || view.PlaceId == place.Id);

        // Off-screen talk only gets words while the mind has room. Live at M7 on the laptop,
        // every conversation queued a call the moment it began, holding the queue at 31 of 32 for
        // a whole run: the scheduler never found room to offer a single reflection, nobody formed
        // a belief, and the myth was never told. Unwatched, a conversation still happens — the
        // ledger moves and both remember it — it just goes unworded, as it did before M5.
        if (!watched && _governor.Stats().QueueDepth >= TargetQueueDepth)
        {
            return;
        }

        var submitted = _governor.Submit(new MindRequest
        {
            Call = CallType.Dialogue,
            Priority = MindRequest.PriorityFor(CallType.Dialogue, playerIsWatching: watched),
            ColonistId = speaker.Id,
            Work = async ct =>
            {
                var colony = Read((c, _) => c);
                var result = await _mind.DialogueAsync(speaker, listener, colony, ct).ConfigureAwait(false);
                Broadcast(Snapshot());

                return new MindCallOutcome
                {
                    PromptTokens = result.Call?.PromptTokens ?? 0,
                    CompletionTokens = result.Call?.CompletionTokens ?? 0,
                    WasFallback = !result.FromModel
                };
            }
        });

        if (submitted)
        {
            _lastDialogue[pair] = _colony.Day;
        }
    }

    private void SubmitIdleThought(Colonist colonist)
    {
        _governor!.Submit(new MindRequest
        {
            Call = CallType.IdleThought,
            Priority = CallPriority.IdleThought,
            ColonistId = colonist.Id,
            Work = async ct =>
            {
                var colony = Read((c, _) => c);
                var (_, call) = await _mind!.ThinkAsync(colonist, colony, ct).ConfigureAwait(false);
                Broadcast(Snapshot());

                return new MindCallOutcome
                {
                    PromptTokens = call?.PromptTokens ?? 0,
                    CompletionTokens = call?.CompletionTokens ?? 0,
                    WasFallback = call is null
                };
            }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _governor?.Start();
        _ = Task.Run(() => ScheduleThinkingAsync(stoppingToken), stoppingToken);

        var timer = new PeriodicTimer(Interval);
        var last = DateTimeOffset.UtcNow;
        var frames = 0;

        _logger.LogInformation("The colony's story is at {Path}. Edit it and the colony tells it that way.", _storyPath);

        // Written before the first tick, so a session that falls over in its first minute has still
        // left something behind.
        if (_chronicleEnabled)
        {
            WriteChronicle();
            _logger.LogInformation("This colony's record is being kept at {Path}.", ChroniclePath);
        }

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // Twice a second, whatever the speed: a story rewritten by hand should take hold
            // while the player is still looking at the file.
            if (++frames % 5 == 0)
            {
                ReadStoryIfChanged();
            }

            var now = DateTimeOffset.UtcNow;
            var elapsed = now - last;
            last = now;

            var ticks = Clock.TicksFor(elapsed);
            if (ticks <= 0)
            {
                // Still amend the record while paused: what was spoken or decided just before
                // someone hit pause is exactly what they will want to have been written down.
                KeepTheRecord();
                continue;
            }

            // Cap the catch-up so a stalled process (a breakpoint, a sleeping laptop) does not
            // simulate hours in one frame and freeze the loop doing it.
            ticks = Math.Min(ticks, 600);

            try
            {
                lock (_gate)
                {
                    for (var i = 0; i < ticks; i++)
                    {
                        _body.Tick();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Simulation tick failed.");
            }

            KeepTheRecord();
            Broadcast(Snapshot());
        }
    }

    /// <summary>
    /// A last amendment on the way out, marked as the end. This is the orderly case; the whole
    /// point of the file is that the disorderly one has already been written to within a couple of
    /// minutes of wherever it stopped.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        WriteChronicle(ended: true);
        _logger.LogInformation("This colony's record is at {Path}.", ChroniclePath);
    }

    public ColonyStateDto Snapshot() => Read((colony, body) => new ColonyStateDto(
        colony.Tick,
        Math.Round(colony.Day, 3),
        Math.Round(colony.TimeOfDay, 4),
        colony.IsNight,
        Math.Round(colony.LoomCharge, 1),
        Math.Round(colony.UpkeepPerDay, 2),
        colony.ArtifactsRecovered,
        colony.Living.Count(),
        body.ActionsCompleted,
        ClockSnapshot(),
        GovernorSnapshot(),
        colony.Living.Select(c => ColonistDto.From(c, colony)).ToList(),
        colony.Map.Places.Select(p => PlaceStateDto.From(p, colony)).ToList(),
        ForkDto.From(colony),
        MythDto.From(colony),
        colony.Lexicon
            .TakeLast(12)
            .Select(w => new CoinedWordDto(w.Term, w.Meaning, colony.Find(w.CoinedById)?.DisplayName ?? w.CoinedById, w.CoinedById, Math.Round(w.Day, 1)))
            .ToList()));

    public GovernorDto? GovernorSnapshot()
    {
        if (_governor is null)
        {
            return null;
        }

        var stats = _governor.Stats();

        return new GovernorDto(
            stats.QueueDepth,
            stats.TokensPerMinute,
            stats.TokensInLastMinute,
            Math.Round(stats.Pressure, 3),
            stats.Dropped,
            stats.Served,
            stats.Fallbacks,
            stats.CurrentCall,
            stats.Throttling);
    }

    public ClockDto ClockSnapshot() => ClockDto.From(Clock, SustainedCallsPerMinute, ThroughputMeasured);

    // ---- subscribers ------------------------------------------------------

    public sealed record Channel(
        System.Threading.Channels.Channel<object> Writer);

    public (Guid Id, System.Threading.Channels.ChannelReader<object> Reader) Subscribe()
    {
        var id = Guid.NewGuid();

        // Bounded and drop-oldest: a client that cannot keep up gets stale frames rather than
        // becoming backpressure on the simulation loop.
        var channel = System.Threading.Channels.Channel.CreateBounded<object>(
            new System.Threading.Channels.BoundedChannelOptions(64)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

        _subscribers[id] = new Channel(channel);
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.Writer.TryComplete();
        }
    }

    private void OnEvent(ColonyEvent colonyEvent)
    {
        // The record sees every line the feed does, and keeps the notable ones. Cheap, and on the
        // path everything already takes — a death or a vote is in the record before the frame ends.
        _chronicle.Notice(colonyEvent);
        Broadcast(new { type = "event", payload = EventDto.From(colonyEvent) });
    }

    private void Broadcast(ColonyStateDto state) =>
        Broadcast(new { type = "state", payload = state });

    private void Broadcast(object message)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.Writer.TryWrite(message);
        }
    }
}
