namespace Emberloom.Core.Simulation;

/// <summary>
/// The colony's random stream, with a state small enough to write down — M11.
/// <para>
/// <see cref="Random"/> cannot be saved. Its internal state is private and not serialisable, so a
/// colony loaded from disk would carry on drawing different numbers from the one that was saved:
/// the same people, the same city, the same memories, and from the next tick onward a different
/// life. For a game whose whole premise is that a seed grows a particular colony, that is not good
/// enough — a save has to be the colony, not a photograph of it.
/// </para>
/// <para>
/// This is splitmix64: sixty-four bits of state, a handful of operations, and a long enough period
/// that nothing here will ever see it repeat. It derives from <see cref="Random"/> so every
/// existing signature keeps working, and only the two members the body actually uses —
/// <see cref="NextDouble"/> and <see cref="Next(int)"/> — plus the <see cref="Sample"/> hook the
/// base class routes through, need overriding.
/// </para>
/// </summary>
public sealed class ColonyRandom : Random
{
    private const ulong Gamma = 0x9E3779B97F4A7C15UL;

    private ulong _state;

    public ColonyRandom(int seed)
    {
        // Mixed rather than used raw, so that seeds 1, 2 and 3 do not start out neighbours.
        _state = unchecked((ulong)seed * 0x2545F4914F6CDD1DUL + Gamma);
    }

    private ColonyRandom(ulong state) => _state = state;

    /// <summary>Everything about where this stream has got to. Sixty-four bits, saved verbatim.</summary>
    public ulong State => _state;

    /// <summary>A stream wound back to exactly where a saved one had reached.</summary>
    public static ColonyRandom FromState(ulong state) => new(state);

    public override double NextDouble() =>
        // 53 bits, the most a double holds exactly, scaled into [0, 1).
        (Mix() >> 11) * (1.0 / 9007199254740992.0);

    protected override double Sample() => NextDouble();

    public override int Next() => (int)(Mix() >> 33);

    public override int Next(int maxValue) =>
        maxValue <= 0 ? 0 : (int)(NextDouble() * maxValue);

    public override int Next(int minValue, int maxValue) =>
        maxValue <= minValue ? minValue : minValue + Next(maxValue - minValue);

    private ulong Mix()
    {
        unchecked
        {
            _state += Gamma;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
