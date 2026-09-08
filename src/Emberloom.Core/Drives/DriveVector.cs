namespace Emberloom.Core.Drives;

/// <summary>
/// Seven values indexed by <see cref="Drive"/>. Used for drive levels, personality weights and
/// the relief an action offers, so the scoring loop is a plain walk over three arrays.
/// </summary>
public sealed class DriveVector
{
    private readonly double[] _values = new double[DriveTable.Count];

    public DriveVector()
    {
    }

    public DriveVector(double fill)
    {
        System.Array.Fill(_values, fill);
    }

    public DriveVector(IReadOnlyDictionary<Drive, double> values, double fallback = 0)
    {
        System.Array.Fill(_values, fallback);
        foreach (var (drive, value) in values)
        {
            _values[(int)drive] = value;
        }
    }

    public double this[Drive drive]
    {
        get => _values[(int)drive];
        set => _values[(int)drive] = value;
    }

    public DriveVector Clone()
    {
        var copy = new DriveVector();
        System.Array.Copy(_values, copy._values, _values.Length);
        return copy;
    }

    public void Clamp(double min, double max)
    {
        for (var i = 0; i < _values.Length; i++)
        {
            _values[i] = Math.Clamp(_values[i], min, max);
        }
    }

    public IReadOnlyDictionary<string, double> ToSchemaDictionary() =>
        DriveTable.All.ToDictionary(DriveTable.Describe, d => Math.Round(this[d], 3));

    public override string ToString() =>
        string.Join(" ", DriveTable.All.Select(d => $"{DriveTable.Describe(d)[..3]}:{this[d]:F2}"));
}

/// <summary>
/// A colonist's current level of need on each drive: 0 is satisfied, 1 is pressing.
/// The body raises these on their own clocks and lowers them when an action delivers relief.
/// </summary>
public sealed class DriveState
{
    private readonly DriveVector _levels;

    public DriveState(DriveVector? initial = null)
    {
        _levels = initial ?? new DriveVector(0.25);
    }

    public double this[Drive drive] => _levels[drive];

    public DriveVector Levels => _levels;

    /// <summary>
    /// Raises every drive by its own per-day rate. Called every tick; drives are needs, so
    /// "decay" in the design table means the satisfaction decays and the need climbs.
    /// </summary>
    public void Advance(double days)
    {
        foreach (var drive in DriveTable.All)
        {
            _levels[drive] += DriveTable.DecayRatePerDay(drive) * days;
        }

        _levels.Clamp(0, 1);
    }

    /// <summary>
    /// Applies the relief an action delivered over <paramref name="scale"/> colony-days of work.
    /// <para>
    /// Relief is <em>proportional to remaining need</em>: you cannot eat more than you are hungry.
    /// Without that, the body has no negative feedback and the whole colony is bistable — supply
    /// even slightly above demand pins every drive at 0 and collapses action selection into noise,
    /// and slightly below pins everything at 1. Proportional relief gives each drive a stable
    /// equilibrium at roughly <c>decay ÷ effective relief rate</c>, which is what lets the
    /// physical drives settle low while Wonder climbs once the city has been picked over.
    /// </para>
    /// </summary>
    public void Relieve(DriveVector relief, double scale = 1.0)
    {
        foreach (var drive in DriveTable.All)
        {
            _levels[drive] -= relief[drive] * scale * _levels[drive];
        }

        _levels.Clamp(0, 1);
    }

    /// <summary>Pushes a single drive up — being ignored, being overruled, standing somewhere dark.</summary>
    public void Aggravate(Drive drive, double amount)
    {
        _levels[drive] = Math.Clamp(_levels[drive] + amount, 0, 1);
    }

    /// <summary>
    /// The drive currently doing the most to shape behaviour, once personality is applied.
    /// Only used for display — scoring reads the whole vector.
    /// </summary>
    public Drive Dominant(PersonalityWeights weights)
    {
        var best = Drive.Sustenance;
        var bestUrgency = double.MinValue;

        foreach (var drive in DriveTable.All)
        {
            var urgency = Urgency(drive, weights[drive]);
            if (urgency > bestUrgency)
            {
                bestUrgency = urgency;
                best = drive;
            }
        }

        return best;
    }

    /// <summary>
    /// The response curve. Squared so a drive that is genuinely pressing dominates one that is
    /// merely present — without it, seven mild needs outvote one urgent one and colonists dither.
    /// </summary>
    public double Urgency(Drive drive)
    {
        var level = _levels[drive];
        return level * level;
    }

    /// <summary>
    /// Urgency as <em>this particular colonist</em> feels it.
    /// <para>
    /// The weight belongs inside the curve, not multiplied onto it afterwards. Applied outside,
    /// a low weight only lowers the score of work on that drive — so the drive goes unmet, climbs
    /// to its ceiling, and dominates the scoring anyway. The observable result was cautious
    /// colonists doing <em>more</em> dangerous salvage than curious ones, having put it off until
    /// it was the most pressing thing they had. Applied inside, caring less about a drive means
    /// genuinely tolerating it sitting high, which is what the trait is supposed to mean.
    /// </para>
    /// </summary>
    public double Urgency(Drive drive, double weight)
    {
        var felt = _levels[drive] * weight;
        return felt * felt;
    }
}
