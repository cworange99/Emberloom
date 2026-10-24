using Emberloom.Core.Colonists;

namespace Emberloom.Core.Simulation;

public enum GameSpeed
{
    Paused = 0,
    Normal = 1,
    Fast = 3,
    Faster = 10,
    Fastest = 60,

    /// <summary>Unattended. Runs as fast as the machine allows and expects nobody to be watching.</summary>
    Overnight = 240
}

/// <summary>
/// Time is the player's, and it costs thought.
/// <para>
/// The governor's ceiling is measured in real-world tokens per minute while the simulation's
/// demand scales with game time, so thinking-per-colony-day falls automatically as you speed up.
/// Nothing enforces that; it is arithmetic. This clock is what makes it legible.
/// </para>
/// </summary>
public sealed class SimulationClock
{
    /// <summary>
    /// Ticks per real second at 1×: a colony-day is one real hour at normal speed. Decided 13
    /// September 2026, after the M7 overnight run. It was two real minutes, which made a 40-day
    /// life shorter than an hour — too quick for anyone to come to know the people in it. At 1×
    /// the colony now keeps the pace of lives you can follow; the faster speeds are for the
    /// experiment, and cost thought per colony-day accordingly.
    /// </summary>
    public const double TicksPerRealSecondAt1X = Colony.TicksPerDay / 3600.0;

    private double _accumulator;

    /// <summary>
    /// The speed the player chose. What the clock actually runs at is <see cref="Speed"/>, which is
    /// this or 1x, whichever is slower, while anything is holding it down.
    /// </summary>
    private GameSpeed _chosen = GameSpeed.Normal;

    /// <summary>
    /// What the clock is actually running at: the player's choice, unless something is holding it
    /// at 1x.
    /// <para>
    /// Two things can hold it — the governor, when a turning point cannot be served fast enough,
    /// and a fork, when the player asked to be slowed for one — and until October 2026 each of them
    /// saved the speed it had interrupted and put it back on release. That is one mechanism too
    /// many, and the two of them got in each other's way in a specific and invisible manner: both
    /// guarded themselves with "the clock is already at 1x, so there is nothing to do", which is
    /// true of the *speed* and false of the *hold*. A governor throttle landing a moment before a
    /// fork opened therefore swallowed that fork's auto-slow entirely, and when the throttle lifted
    /// — as it does the moment the budget frees up — the colony went back to 60x with the vote
    /// still open and ran several colony-days of deliberation at speed. Which Jeremy watched happen.
    /// </para>
    /// <para>
    /// Holds do not stack or save anything. Each is a flag, the clock runs at the slowest thing
    /// holding it, and the player's own choice clears them all.
    /// </para>
    /// </summary>
    public GameSpeed Speed =>
        (ThrottledByGovernor || SlowedForEvent) && _chosen > GameSpeed.Normal
            ? GameSpeed.Normal
            : _chosen;

    /// <summary>
    /// Drops to 1× when a Deliberation fires, the way a strategy game halts on an alert. The
    /// player looks up because something is being decided — and the speed they were at comes back
    /// when the fork resolves.
    /// </summary>
    public bool AutoSlowOnEvent { get; set; } = true;

    /// <summary>
    /// True while a fork is holding the speed down. Like a governor throttle, and unlike the
    /// player's own choice, it is temporary.
    /// </summary>
    public bool SlowedForEvent { get; private set; }

    /// <summary>
    /// Set by the governor when it cannot serve an undroppable call at the current speed. A colony
    /// that fast-forwards past its own turning points is broken, so the simulation slows itself.
    /// </summary>
    public bool ThrottledByGovernor { get; private set; }

    /// <summary>
    /// The player changes speed from an HTTP thread and the governor throttles and releases from its
    /// own. Every change of speed goes through this lock, so the flags and the speed they imply are
    /// never read halfway through a change.
    /// </summary>
    private readonly object _gate = new();

    public event Action<GameSpeed>? SpeedChanged;

    /// <summary>
    /// The player's choice, and it always wins. It ends every hold outright, so neither a throttle
    /// nor a fork can put back a speed the player has since moved away from.
    /// </summary>
    public void SetSpeed(GameSpeed speed) => Change(() =>
    {
        _chosen = speed;
        ThrottledByGovernor = false;
        SlowedForEvent = false;
    });

    /// <summary>Called by the governor when an undroppable call cannot be served fast enough.</summary>
    public void ThrottleForThought() => Change(() => ThrottledByGovernor = true);

    public void ReleaseThrottle() => Change(() => ThrottledByGovernor = false);

    /// <summary>
    /// Auto-slow when a Deliberation fires. Distinct from a governor throttle, and — since 19
    /// September 2026 — temporary in the same way: the hold lifts when the fork resolves.
    /// <para>
    /// Never at Overnight, which is defined as nobody watching. Slowing down to make a player look
    /// up is meaningless when the whole point of the speed is that there is no player there.
    /// </para>
    /// </summary>
    public void SlowForEvent() => Change(() =>
    {
        if (AutoSlowOnEvent && _chosen != GameSpeed.Overnight)
        {
            SlowedForEvent = true;
        }
    });

    /// <summary>The fork is decided; lift its hold. The player's choice comes back by itself.</summary>
    public void ResumeAfterEvent() => Change(() => SlowedForEvent = false);

    /// <summary>
    /// Applies a change under the lock and tells the world only if the clock's actual speed moved.
    /// Every mutator goes through here, so there is one place where the event can be raised and it
    /// is never raised while the lock is held.
    /// </summary>
    private void Change(Action change)
    {
        GameSpeed after;

        lock (_gate)
        {
            var before = Speed;
            change();
            after = Speed;

            if (before == after)
            {
                return;
            }
        }

        SpeedChanged?.Invoke(after);
    }

    /// <summary>How many ticks to run for a given slice of real time. Zero while paused.</summary>
    public int TicksFor(TimeSpan realElapsed)
    {
        if (Speed == GameSpeed.Paused)
        {
            _accumulator = 0;
            return 0;
        }

        _accumulator += realElapsed.TotalSeconds * TicksPerRealSecondAt1X * (int)Speed;
        var ticks = (int)_accumulator;
        _accumulator -= ticks;
        return ticks;
    }

    /// <summary>Colony-days elapsing per real minute at the current speed.</summary>
    public double ColonyDaysPerRealMinute =>
        Speed == GameSpeed.Paused
            ? 0
            : TicksPerRealSecondAt1X * (int)Speed * 60.0 / Colony.TicksPerDay;

    /// <summary>
    /// The Contemplation readout that sits beside the speed control: how many deep-lane calls the
    /// colony can afford per colony-day at this speed. Visibly drops as you accelerate, which
    /// turns an engineering limit into a legible choice.
    /// </summary>
    /// <param name="sustainedCallsPerMinute">Measured at M0 for this machine.</param>
    public double CallsPerColonyDay(double sustainedCallsPerMinute)
    {
        var daysPerMinute = ColonyDaysPerRealMinute;
        return daysPerMinute <= 0 ? double.PositiveInfinity : sustainedCallsPerMinute / daysPerMinute;
    }

    public static IReadOnlyList<GameSpeed> AllSpeeds =>
    [
        GameSpeed.Paused,
        GameSpeed.Normal,
        GameSpeed.Fast,
        GameSpeed.Faster,
        GameSpeed.Fastest,
        GameSpeed.Overnight
    ];

    public static string Label(GameSpeed speed) => speed switch
    {
        GameSpeed.Paused => "paused",
        GameSpeed.Overnight => "overnight",
        _ => $"{(int)speed}x"
    };
}
