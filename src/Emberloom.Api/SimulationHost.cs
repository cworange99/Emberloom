using System.Collections.Concurrent;
using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Mind;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Governor;
using Emberloom.Mind.Ollama;
using Emberloom.Core.Memory;
using Emberloom.Core.Events;
using Emberloom.Core.Simulation;

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

        // Measured at M0 on this machine. From M2 the governor owns this; until then it only
        // feeds the Contemplation readout, which is worth showing from M1 so the cost of speed
        // is visible before anything is actually spending it.
        SustainedCallsPerMinute = configuration.GetValue("Simulation:SustainedCallsPerMinute", 4.8);

        _random = new Random(seed);
        _colony = ColonyFactory.Create(colonists, _random);
        _body = new BodySimulation(_colony, _random);
        Clock = new SimulationClock();

        _colony.Feed.Appended += OnEvent;
        QueueKindlingForEveryone();
    }

    public SimulationClock Clock { get; }

    public bool MindConfigured => _mind is not null;

    public double SustainedCallsPerMinute { get; }

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
        lock (_gate)
        {
            _colony.Feed.Appended -= OnEvent;

            _random = new Random(seed);
            _colony = ColonyFactory.Create(colonists, _random);
            _body = new BodySimulation(_colony, _random);
            _colony.Feed.Appended += OnEvent;
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
    private void SubmitKindling(Colonist colonist)
    {
        if (_mind is null || _governor is null)
        {
            return;
        }

        _governor.Submit(new MindRequest
        {
            Call = CallType.Kindling,
            Priority = CallPriority.Kindling,
            ColonistId = colonist.Id,
            Undroppable = true,
            Work = async ct =>
            {
                // One path for Kindling, shared with the tests: MindService makes the call outside
                // the colony lock, commits the result under it, and falls back — with a trace line
                // saying so — when the mind is not there. This used to be a second copy of that
                // logic which threw on an unreachable mind instead; the governor treats a throw as
                // lost work, so a colonist kindled while Ollama was down got no trace line at all.
                var colony = Read((c, _) => c);
                var result = await _mind.KindleAsync(colonist, colony, colonist.Seed, ct: ct).ConfigureAwait(false);

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

        // The queue is a work buffer, not a firehose. Offering everything every tick would refuse
        // hundreds of requests a minute and make the drop count meaningless — a drop should mean
        // "something worth doing lost to something worth more", not "the scheduler shouted again".
        const int TargetQueueDepth = 6;

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
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
                var result = await _mind!.ReflectAsync(colonist, colony, ct).ConfigureAwait(false);
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

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - last;
            last = now;

            var ticks = Clock.TicksFor(elapsed);
            if (ticks <= 0)
            {
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

            Broadcast(Snapshot());
        }
    }

    public ColonyStateDto Snapshot() => Read((colony, body) => new ColonyStateDto(
        colony.Tick,
        Math.Round(colony.Day, 3),
        Math.Round(colony.TimeOfDay, 4),
        colony.IsNight,
        Math.Round(colony.LoomCharge, 1),
        colony.ArtifactsRecovered,
        colony.Living.Count(),
        body.ActionsCompleted,
        ClockSnapshot(),
        GovernorSnapshot(),
        colony.Living.Select(c => ColonistDto.From(c, colony)).ToList()));

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

    public ClockDto ClockSnapshot() => ClockDto.From(Clock, SustainedCallsPerMinute);

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

    private void OnEvent(ColonyEvent colonyEvent) =>
        Broadcast(new { type = "event", payload = EventDto.From(colonyEvent) });

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
