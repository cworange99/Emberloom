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

    public GameSpeed Speed { get; private set; } = GameSpeed.Normal;

    /// <summary>
    /// Drops to 1× when a Deliberation fires, the way a strategy game halts on an alert. The
    /// player looks up because something is being decided.
    /// </summary>
    public bool AutoSlowOnEvent { get; set; } = true;

    /// <summary>
    /// Set by the governor when it cannot serve an undroppable call at the current speed. A colony
    /// that fast-forwards past its own turning points is broken, so the simulation slows itself.
    /// </summary>
    public bool ThrottledByGovernor { get; private set; }

    private GameSpeed _speedBeforeThrottle = GameSpeed.Normal;

    /// <summary>
    /// The player changes speed from an HTTP thread and the governor throttles and releases from its
    /// own. Every change of speed goes through this lock, so a throttle that starts just as the
    /// player pauses cannot save the old speed after the pause has landed and put it back later —
    /// which would look like the game un-pausing itself.
    /// </summary>
    private readonly object _gate = new();

    public event Action<GameSpeed>? SpeedChanged;

    /// <summary>
    /// The player's choice, and it always wins. It ends any governor throttle outright and forgets
    /// the speed the throttle saved, so a later release cannot overrule it.
    /// </summary>
    public void SetSpeed(GameSpeed speed)
    {
        lock (_gate)
        {
            // "No change" only when there is no throttle to end. Choosing 1x while throttled used to
            // count as no change — the throttle was already holding 1x — so the throttle stayed and
            // put back the faster speed on release, overruling what the player had just picked.
            if (Speed == speed && !ThrottledByGovernor)
            {
                return;
            }

            Speed = speed;
            _speedBeforeThrottle = speed;
            ThrottledByGovernor = false;
        }

        SpeedChanged?.Invoke(speed);
    }

    /// <summary>Called by the governor when an undroppable call cannot be served fast enough.</summary>
    public void ThrottleForThought()
    {
        lock (_gate)
        {
            if (ThrottledByGovernor || Speed <= GameSpeed.Normal)
            {
                return;
            }

            _speedBeforeThrottle = Speed;
            Speed = GameSpeed.Normal;
            ThrottledByGovernor = true;
        }

        SpeedChanged?.Invoke(GameSpeed.Normal);
    }

    public void ReleaseThrottle()
    {
        GameSpeed restored;

        lock (_gate)
        {
            if (!ThrottledByGovernor)
            {
                return;
            }

            restored = _speedBeforeThrottle;
            Speed = restored;
            ThrottledByGovernor = false;
        }

        SpeedChanged?.Invoke(restored);
    }

    /// <summary>Auto-slow when a Deliberation fires. Distinct from a governor throttle.</summary>
    public void SlowForEvent()
    {
        if (AutoSlowOnEvent && Speed > GameSpeed.Normal)
        {
            SetSpeed(GameSpeed.Normal);
        }
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
