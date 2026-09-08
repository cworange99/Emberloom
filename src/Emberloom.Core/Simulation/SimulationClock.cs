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
    /// <summary>Ticks per real second at 1×. A colony-day is two real minutes at normal speed.</summary>
    public const double TicksPerRealSecondAt1X = 2.0;

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

    public event Action<GameSpeed>? SpeedChanged;

    public void SetSpeed(GameSpeed speed)
    {
        if (Speed == speed)
        {
            return;
        }

        Speed = speed;
        ThrottledByGovernor = false;
        SpeedChanged?.Invoke(speed);
    }

    /// <summary>Called by the governor when an undroppable call cannot be served fast enough.</summary>
    public void ThrottleForThought()
    {
        if (ThrottledByGovernor || Speed <= GameSpeed.Normal)
        {
            return;
        }

        _speedBeforeThrottle = Speed;
        Speed = GameSpeed.Normal;
        ThrottledByGovernor = true;
        SpeedChanged?.Invoke(Speed);
    }

    public void ReleaseThrottle()
    {
        if (!ThrottledByGovernor)
        {
            return;
        }

        Speed = _speedBeforeThrottle;
        ThrottledByGovernor = false;
        SpeedChanged?.Invoke(Speed);
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
