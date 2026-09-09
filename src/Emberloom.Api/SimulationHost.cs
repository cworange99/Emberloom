using System.Collections.Concurrent;
using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Mind;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Ollama;
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
    private readonly System.Threading.Channels.Channel<string> _toKindle =
        System.Threading.Channels.Channel.CreateUnbounded<string>();

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
            });
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
            _toKindle.Writer.TryWrite(colonist.Id);
        }
    }

    /// <summary>
    /// Kindles queued colonists one at a time. Strictly sequential, because a second in-flight
    /// request halves both — that rule is from the design and holds whether or not the governor
    /// exists yet. What this deliberately is <em>not</em> is the governor: no priorities, no
    /// budget, no dropping. Those arrive with M2.
    /// </summary>
    private async Task KindleLoopAsync(CancellationToken stoppingToken)
    {
        if (_mind is null)
        {
            return;
        }

        await foreach (var colonistId in _toKindle.Reader.ReadAllAsync(stoppingToken))
        {
            var colonist = Read((colony, _) => colony.Find(colonistId));
            if (colonist is null || colonist.IsKindled)
            {
                continue;
            }

            try
            {
                // The colony is mutated on the tick thread, so the write-back has to take the
                // same lock. The call itself is made outside it — holding the lock across a
                // multi-second inference would stall the simulation, which is the one thing
                // Layer 2 is never allowed to do.
                var seedText = colonist.Seed;
                var result = await KindlingCall.ExecuteAsync(_mind.Client,
                    new KindlingSeed { Designation = colonist.Designation, Seed = seedText },
                    stoppingToken).ConfigureAwait(false);

                lock (_gate)
                {
                    ApplyKindling(colonist, result);
                }

                _logger.LogInformation(
                    "Kindled {Designation} ({Source}).",
                    colonist.Designation,
                    result.FromModel ? "model" : "fallback");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kindling {Designation} failed; leaving the stub in place.", colonist.Designation);

                lock (_gate)
                {
                    ApplyKindling(colonist, KindlingCall.Fallback(
                        new KindlingSeed { Designation = colonist.Designation, Seed = colonist.Seed }));
                }
            }

            Broadcast(Snapshot());
        }
    }

    private void ApplyKindling(Colonist colonist, KindlingResult result)
    {
        colonist.Personality = result.Personality;
        colonist.Weights = result.Weights;
        colonist.NameCandidates = result.NameCandidates;
        colonist.IsKindled = result.FromModel;

        colonist.RecordCall(new ModelCallRecord
        {
            CallType = "Kindling",
            Day = _colony.Day,
            Model = result.Call?.Model ?? "none",
            Lane = "deep",
            PromptTokens = result.Call?.PromptTokens ?? 0,
            CompletionTokens = result.Call?.CompletionTokens ?? 0,
            Seconds = result.Call?.TotalDuration.TotalSeconds ?? 0,
            Produced = result.FromModel
                ? $"personality, {result.NameCandidates.Count} name candidates, seven weights"
                : "fallback stub — the mind was unavailable",
            WasFallback = !result.FromModel
        });

        _colony.Log(
            result.FromModel ? EventLayer.Mind : EventLayer.World,
            result.FromModel
                ? $"{colonist.Designation} woke. {result.SeedSummary}"
                : $"{colonist.Designation} woke, but nothing was there to write them.",
            colonist,
            notable: true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = Task.Run(() => KindleLoopAsync(stoppingToken), stoppingToken);

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
        colony.Living.Select(c => ColonistDto.From(c, colony)).ToList()));

    public ClockDto ClockSnapshot() => new(
        Clock.Speed.ToString(),
        SimulationClock.Label(Clock.Speed),
        Clock.AutoSlowOnEvent,
        Clock.ThrottledByGovernor,
        Math.Round(Clock.ColonyDaysPerRealMinute, 3),
        Math.Round(Clock.CallsPerColonyDay(SustainedCallsPerMinute), 2),
        SustainedCallsPerMinute);

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
