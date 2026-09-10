using Emberloom.Mind.Calls;

namespace Emberloom.Mind.Governor;

/// <summary>
/// The design's priority order, highest first. Two things sit above everything and are also
/// undroppable — a colony that fast-forwards past its own turning points is broken, and no amount
/// of graceful degradation fixes that.
/// </summary>
public enum CallPriority
{
    IdleThought = 0,
    Coinage = 1,
    OffScreenDialogue = 2,
    Reflection = 3,
    Kindling = 4,
    Deliberation = 5,

    /// <summary>Whatever the player is looking at right now. Outranks everything.</summary>
    PlayerFocus = 6
}

public sealed record MindCallOutcome
{
    public static readonly MindCallOutcome Nothing = new() { PromptTokens = 0, CompletionTokens = 0 };

    public required int PromptTokens { get; init; }
    public required int CompletionTokens { get; init; }

    /// <summary>True when the mind was unreachable and a templated stub stood in.</summary>
    public bool WasFallback { get; init; }

    public int TotalTokens => PromptTokens + CompletionTokens;
}

public sealed record MindRequest
{
    /// <summary>
    /// Which colony this belongs to. There is only ever one locally, but the budget is keyed by it
    /// from the start — a hosted deployment needs per-session fairness, and retrofitting that onto
    /// a single global queue is the rewrite this shape exists to avoid.
    /// </summary>
    public string SessionId { get; init; } = "local";

    public required CallType Call { get; init; }

    public required CallPriority Priority { get; init; }

    public required string ColonistId { get; init; }

    /// <summary>
    /// Dropped only over the queue-depth cap, and never if this is set. Kindling and Deliberation
    /// set it; if the queue cannot serve them, the simulation slows itself instead.
    /// </summary>
    public bool Undroppable { get; init; }

    /// <summary>Breaks ties within a priority band. Reflection sorts on accumulated salience.</summary>
    public double Weight { get; init; }

    public required Func<CancellationToken, Task<MindCallOutcome>> Work { get; init; }

    public DateTimeOffset QueuedAt { get; init; } = DateTimeOffset.UtcNow;

    public static CallPriority PriorityFor(CallType call, bool playerIsWatching = false) =>
        playerIsWatching
            ? CallPriority.PlayerFocus
            : call switch
            {
                CallType.Deliberation => CallPriority.Deliberation,
                CallType.Kindling => CallPriority.Kindling,
                CallType.Reflection => CallPriority.Reflection,
                CallType.Dialogue => CallPriority.OffScreenDialogue,
                CallType.Coinage => CallPriority.Coinage,
                _ => CallPriority.IdleThought
            };

    public static bool IsUndroppable(CallType call) =>
        call is CallType.Kindling or CallType.Deliberation;
}

/// <summary>
/// A rolling record of what has been spent, in <em>real-world</em> time.
/// <para>
/// Real-world rather than game time is the whole trick behind §6: demand scales with how fast the
/// player runs the simulation, while this ceiling does not, so thinking-per-colony-day falls as
/// you accelerate without anything enforcing it. The Contemplation readout just makes that
/// arithmetic visible.
/// </para>
/// </summary>
public sealed class BudgetLedger
{
    private readonly record struct Spend(DateTimeOffset At, int Tokens);

    private readonly Queue<Spend> _window = new();
    private readonly TimeSpan _period = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();

    private int _tokensInWindow;

    public BudgetLedger(int tokensPerMinute)
    {
        TokensPerMinute = tokensPerMinute;
    }

    /// <summary>Measured at M0 for the machine actually serving. Zero or less means no ceiling.</summary>
    public int TokensPerMinute { get; set; }

    public int TokensInLastMinute
    {
        get
        {
            lock (_gate)
            {
                Trim(DateTimeOffset.UtcNow);
                return _tokensInWindow;
            }
        }
    }

    public bool HasHeadroom
    {
        get
        {
            if (TokensPerMinute <= 0)
            {
                return true;
            }

            lock (_gate)
            {
                Trim(DateTimeOffset.UtcNow);
                return _tokensInWindow < TokensPerMinute;
            }
        }
    }

    public void Record(int tokens)
    {
        if (tokens <= 0)
        {
            return;
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _window.Enqueue(new Spend(now, tokens));
            _tokensInWindow += tokens;
            Trim(now);
        }
    }

    private void Trim(DateTimeOffset now)
    {
        while (_window.Count > 0 && now - _window.Peek().At > _period)
        {
            _tokensInWindow -= _window.Dequeue().Tokens;
        }

        if (_tokensInWindow < 0)
        {
            _tokensInWindow = 0;
        }
    }
}
