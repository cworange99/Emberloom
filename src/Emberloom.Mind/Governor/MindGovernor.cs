using Emberloom.Mind.Calls;

namespace Emberloom.Mind.Governor;

public sealed record GovernorStats
{
    public required int QueueDepth { get; init; }
    public required int TokensPerMinute { get; init; }
    public required int TokensInLastMinute { get; init; }
    public required int Dropped { get; init; }
    public required int Served { get; init; }
    public required int Fallbacks { get; init; }
    public required string? CurrentCall { get; init; }
    public required bool Throttling { get; init; }

    /// <summary>Share of the real-time ceiling currently spent. Above 1 means the queue is waiting.</summary>
    public double Pressure => TokensPerMinute <= 0 ? 0 : (double)TokensInLastMinute / TokensPerMinute;
}

/// <summary>
/// One component owns the answer to "how much thinking is happening right now."
/// <para>
/// It enforces a single concurrent request, a rolling real-world token ceiling, a per-colonist
/// cooldown so one dramatic character cannot monopolise the colony's thinking, and a queue depth
/// beyond which the lowest-priority work is dropped rather than queued forever. Kindling and
/// Deliberation are undroppable: if they cannot be served at the current speed, the governor asks
/// the simulation to slow itself instead.
/// </para>
/// <para>
/// Budgets are keyed by session even though there is only one locally. That is the cheap half of
/// the hosted decision — a per-session budget with one session is just a budget, but retrofitting
/// per-tenant fairness onto a global queue later would be a rewrite.
/// </para>
/// </summary>
public sealed class MindGovernor : IDisposable, IAsyncDisposable
{
    private readonly List<MindRequest> _queue = [];
    private readonly Dictionary<string, BudgetLedger> _budgets = [];
    private readonly Dictionary<string, DateTimeOffset> _cooldowns = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stopping = new();

    private readonly int _defaultTokensPerMinute;
    private readonly int _maxQueueDepth;
    private readonly TimeSpan _perColonistCooldown;

    private Task? _worker;
    private string? _currentCall;
    private int _dropped;
    private int _served;
    private int _fallbacks;
    private bool _throttling;

    public MindGovernor(
        int tokensPerMinute,
        int maxQueueDepth = 32,
        TimeSpan? perColonistCooldown = null)
    {
        _defaultTokensPerMinute = tokensPerMinute;
        _maxQueueDepth = maxQueueDepth;
        _perColonistCooldown = perColonistCooldown ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>
    /// Raised when an undroppable call cannot be served inside the current budget. The host slows
    /// the simulation until it can — this is the one place time bends for function.
    /// </summary>
    public event Action<bool>? ThrottleRequested;

    public void Start()
    {
        _worker ??= Task.Run(() => WorkAsync(_stopping.Token));
    }

    public BudgetLedger BudgetFor(string sessionId)
    {
        lock (_gate)
        {
            if (!_budgets.TryGetValue(sessionId, out var ledger))
            {
                ledger = new BudgetLedger(_defaultTokensPerMinute);
                _budgets[sessionId] = ledger;
            }

            return ledger;
        }
    }

    /// <summary>
    /// Offers a request to the queue. Returns false when it was refused outright — the queue is at
    /// depth and this is the least valuable thing in it. Refusal is normal and expected at speed;
    /// it is how thinking-per-colony-day falls when the player accelerates.
    /// </summary>
    public bool Submit(MindRequest request)
    {
        lock (_gate)
        {
            // One call per colonist per cooldown, so a single dramatic character cannot spend the
            // whole colony's budget on itself. Undroppable work ignores it.
            if (!request.Undroppable
                && _cooldowns.TryGetValue(request.ColonistId, out var until)
                && DateTimeOffset.UtcNow < until)
            {
                _dropped++;
                return false;
            }

            if (_queue.Count >= _maxQueueDepth)
            {
                var weakest = _queue
                    .Where(r => !r.Undroppable)
                    .OrderBy(r => r.Priority)
                    .ThenBy(r => r.Weight)
                    .FirstOrDefault();

                // Nothing droppable and nothing cheaper than this: refuse rather than grow without
                // bound. A queue that never refuses is a queue that serves stale work forever.
                if (weakest is null || (!request.Undroppable && weakest.Priority >= request.Priority))
                {
                    _dropped++;
                    return false;
                }

                _queue.Remove(weakest);
                _dropped++;
            }

            _queue.Add(request);
        }

        _signal.Release();
        return true;
    }

    public GovernorStats Stats()
    {
        lock (_gate)
        {
            var ledger = BudgetForUnlocked("local");

            return new GovernorStats
            {
                QueueDepth = _queue.Count,
                TokensPerMinute = ledger.TokensPerMinute,
                TokensInLastMinute = ledger.TokensInLastMinute,
                Dropped = _dropped,
                Served = _served,
                Fallbacks = _fallbacks,
                CurrentCall = _currentCall,
                Throttling = _throttling
            };
        }
    }

    private BudgetLedger BudgetForUnlocked(string sessionId)
    {
        if (!_budgets.TryGetValue(sessionId, out var ledger))
        {
            ledger = new BudgetLedger(_defaultTokensPerMinute);
            _budgets[sessionId] = ledger;
        }

        return ledger;
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                var next = TakeNext(out var mustThrottle);

                SetThrottle(mustThrottle);

                if (next is null)
                {
                    break;
                }

                await RunAsync(next, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Highest priority first, then heaviest, then oldest. Returns null while the budget is spent —
    /// and reports whether the thing being held up is undroppable, which is what makes the
    /// simulation slow rather than skip a turning point.
    /// </summary>
    private MindRequest? TakeNext(out bool mustThrottle)
    {
        mustThrottle = false;

        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                return null;
            }

            var head = _queue
                .OrderByDescending(r => r.Priority)
                .ThenByDescending(r => r.Weight)
                .ThenBy(r => r.QueuedAt)
                .First();

            if (!BudgetForUnlocked(head.SessionId).HasHeadroom)
            {
                // Over the real-time ceiling. Droppable work simply waits its turn; an undroppable
                // call waiting here is the signal to slow the simulation until it can be served.
                mustThrottle = head.Undroppable;
                return null;
            }

            _queue.Remove(head);
            _currentCall = $"{head.Call} · {head.ColonistId}";

            if (!head.Undroppable)
            {
                _cooldowns[head.ColonistId] = DateTimeOffset.UtcNow + _perColonistCooldown;
            }

            return head;
        }
    }

    private async Task RunAsync(MindRequest request, CancellationToken ct)
    {
        try
        {
            var outcome = await request.Work(ct).ConfigureAwait(false);

            lock (_gate)
            {
                BudgetForUnlocked(request.SessionId).Record(outcome.TotalTokens);
                _served++;

                if (outcome.WasFallback)
                {
                    _fallbacks++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A failed call is not a failed governor. The call type itself is responsible for
            // falling back; anything that escapes here is already lost work, not a reason to stop
            // serving the queue.
            lock (_gate)
            {
                _fallbacks++;
            }
        }
        finally
        {
            lock (_gate)
            {
                _currentCall = null;
            }
        }
    }

    private void SetThrottle(bool throttling)
    {
        bool changed;

        lock (_gate)
        {
            changed = _throttling != throttling;
            _throttling = throttling;
        }

        if (changed)
        {
            ThrottleRequested?.Invoke(throttling);
        }
    }

    /// <summary>Synchronous teardown, for callers that are not async. Stops the worker and waits.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _stopping.Dispose();
        _signal.Dispose();
    }
}
