namespace Emberloom.Core.Memory;

/// <summary>
/// What the body writes down, constantly and cheaply. No prose — Layer 2's job is not to generate
/// these, it is to periodically read a batch and decide what they meant.
/// </summary>
public sealed record EpisodicMemory
{
    public required string Id { get; init; }

    public required long Tick { get; init; }

    public required double Day { get; init; }

    /// <summary>Colonist ids involved, the owner first.</summary>
    public required IReadOnlyList<string> Actors { get; init; }

    public required string Verb { get; init; }

    public string? Object { get; init; }

    public required string PlaceId { get; init; }

    /// <summary>-1 to +1. Whether this went well for the owner.</summary>
    public required double Valence { get; init; }

    /// <summary>0 to 1. How much it registered at all.</summary>
    public required double Intensity { get; init; }

    /// <summary>Bumped every time retrieval surfaces this. Feeds salience.</summary>
    public int RecallCount { get; set; }

    /// <summary>Set once a reflection has folded this into a consolidated memory.</summary>
    public bool Consolidated { get; set; }

    /// <summary>
    /// Generative-Agents style scoring: intensity, decayed by age, lifted by how often it has come
    /// back. What the reflection scheduler sorts on, and what pruning reads.
    /// </summary>
    public double Salience(double currentDay, double halfLifeDays = 9.0)
    {
        var age = Math.Max(0, currentDay - Day);
        var recency = Math.Pow(0.5, age / halfLifeDays);
        var recallBonus = 1.0 + Math.Min(RecallCount, 20) * 0.05;
        return Intensity * Math.Abs(Valence) * recency * recallBonus;
    }

    public string Describe() =>
        Object is null ? Verb : $"{Verb} {Object}";
}

/// <summary>
/// A written memory standing in for a batch of episodic rows, produced by Reflection. In M1
/// nothing writes these — the type exists so the body's pruning already respects them.
/// </summary>
public sealed record ConsolidatedMemory
{
    public required string Id { get; init; }
    public required string OwnerId { get; init; }
    public required double Day { get; init; }
    public required string Text { get; init; }
    public required double Valence { get; init; }
    public required IReadOnlyList<string> SourceMemoryIds { get; init; }
    public int RecallCount { get; set; }
    public float[]? Embedding { get; set; }

    public double Salience(double currentDay, double halfLifeDays = 30.0)
    {
        var recency = Math.Pow(0.5, Math.Max(0, currentDay - Day) / halfLifeDays);
        return (0.4 + Math.Abs(Valence) * 0.6) * recency * (1.0 + Math.Min(RecallCount, 30) * 0.04);
    }
}

/// <summary>
/// A durable claim about the world. Never a free-floating string — it carries the ids of the
/// memories that produced it, which is what makes the inspector's traceable chain possible.
/// </summary>
public sealed record Belief
{
    public required string Id { get; init; }
    public required string OwnerId { get; init; }
    public required double FormedOnDay { get; init; }
    public required string Claim { get; init; }
    public required IReadOnlyList<string> SourceMemoryIds { get; init; }
    public required double Confidence { get; init; }

    /// <summary>Set when this belief runs against what the colony as a whole holds.</summary>
    public bool ContradictsColonyMyth { get; set; }

    public float[]? Embedding { get; set; }
}
