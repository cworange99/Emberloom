namespace Emberloom.Core.Drives;

/// <summary>
/// How much this specific colonist cares about each drive relative to baseline. 1.0 is ordinary;
/// Sela's 1.6 on Wonder is why she is on the ninth floor instead of foraging.
/// <para>
/// This is the seam between the two layers. Kindling writes these once, Reflection drifts them,
/// and the body reads them on every tick without ever calling a model. In M1 they are
/// hand-authored constants from <see cref="Archetypes"/>.
/// </para>
/// </summary>
public sealed class PersonalityWeights
{
    public const double Min = 0.2;
    public const double Max = 2.0;

    private readonly DriveVector _weights;

    public PersonalityWeights(DriveVector? weights = null)
    {
        _weights = weights ?? new DriveVector(1.0);
        _weights.Clamp(Min, Max);
    }

    public double this[Drive drive] => _weights[drive];

    public DriveVector Vector => _weights;

    /// <summary>Applied by Reflection when a colonist's experience has genuinely moved them.</summary>
    public void Drift(Drive drive, double delta)
    {
        _weights[drive] = Math.Clamp(_weights[drive] + delta, Min, Max);
    }

    public PersonalityWeights Clone() => new(_weights.Clone());

    /// <summary>
    /// Weight crossover with mutation — Creatures' genetics, moved up a level from bodies to
    /// values. Each weight is drawn from one parent or blended, then nudged.
    /// </summary>
    public static PersonalityWeights Crossover(
        PersonalityWeights left,
        PersonalityWeights right,
        Random random,
        double mutationRate = 0.15,
        double mutationScale = 0.25)
    {
        var child = new DriveVector();

        foreach (var drive in DriveTable.All)
        {
            child[drive] = random.NextDouble() switch
            {
                < 0.4 => left[drive],
                < 0.8 => right[drive],
                _ => (left[drive] + right[drive]) / 2
            };

            if (random.NextDouble() < mutationRate)
            {
                child[drive] += (random.NextDouble() * 2 - 1) * mutationScale;
            }
        }

        return new PersonalityWeights(child);
    }

    public static PersonalityWeights From(params (Drive Drive, double Weight)[] overrides)
    {
        var vector = new DriveVector(1.0);
        foreach (var (drive, weight) in overrides)
        {
            vector[drive] = weight;
        }

        return new PersonalityWeights(vector);
    }
}

/// <summary>
/// Hand-authored personalities for M1, standing in for what Kindling will write from M2 onward.
/// They exist to prove the seam works: swapping an archetype must visibly change behaviour with
/// no other code change.
/// </summary>
public static class Archetypes
{
    public sealed record Archetype(string Name, string Blurb, PersonalityWeights Weights);

    public static readonly Archetype Curious = new(
        "curious",
        "Curious past the point of caution. Works alone by preference.",
        PersonalityWeights.From(
            (Drive.Wonder, 1.6),
            (Drive.Standing, 1.2),
            (Drive.Safety, 0.7),
            (Drive.Belonging, 0.8)));

    public static readonly Archetype Steady = new(
        "steady",
        "Does the work and does not collect on it. Watches before acting.",
        PersonalityWeights.From(
            (Drive.Meaning, 1.4),
            (Drive.Safety, 1.2),
            (Drive.Standing, 0.6),
            (Drive.Wonder, 0.8)));

    public static readonly Archetype Sociable = new(
        "sociable",
        "Talks the others around. Would rather be agreed with than be right.",
        PersonalityWeights.From(
            (Drive.Belonging, 1.6),
            (Drive.Standing, 1.3),
            (Drive.Wonder, 0.7),
            (Drive.Rest, 0.9)));

    public static readonly Archetype Cautious = new(
        "cautious",
        "Keeps to cleared ground and lit rooms. Notices what others walk past.",
        PersonalityWeights.From(
            (Drive.Safety, 1.7),
            (Drive.Rest, 1.2),
            (Drive.Wonder, 0.5),
            (Drive.Standing, 0.7)));

    public static readonly Archetype Restless = new(
        "restless",
        "Cannot stand a repeated day. Starts things.",
        PersonalityWeights.From(
            (Drive.Meaning, 1.5),
            (Drive.Wonder, 1.3),
            (Drive.Rest, 0.6),
            (Drive.Sustenance, 0.8)));

    public static readonly IReadOnlyList<Archetype> All = [Curious, Steady, Sociable, Cautious, Restless];

    public static Archetype Random(Random random) => All[random.Next(All.Count)];
}
