using Emberloom.Core.Actions;
using Emberloom.Core.Drives;
using Emberloom.Core.Memory;

namespace Emberloom.Core.Colonists;

public enum LineageKind
{
    /// <summary>The Loom spent charge on this one. Deliberate, expensive, political.</summary>
    Printed,

    /// <summary>Two colonists past a bond threshold. Emergent, cheap, personal.</summary>
    Paired
}

/// <summary>
/// One mind and the body it runs. Everything on here except <see cref="Personality"/>,
/// <see cref="Name"/> and the consolidated tiers of <see cref="Memory"/> is written by Layer 1.
/// </summary>
public sealed class Colonist
{
    public required string Id { get; init; }

    /// <summary>What the Loom called them. Printed minds arrive with this and nothing else.</summary>
    public required string Designation { get; init; }

    /// <summary>
    /// Null until they have done something they consider worth being named for. The colony
    /// remembers who named themselves what, and after what.
    /// </summary>
    public string? Name { get; set; }

    public string DisplayName => Name ?? Designation;

    public required LineageKind Lineage { get; init; }

    public IReadOnlyList<string> ParentIds { get; init; } = [];

    public required double BornOnDay { get; init; }

    /// <summary>Prose written by Kindling. Hand-authored from an archetype in M1.</summary>
    public required string Personality { get; set; }

    public required PersonalityWeights Weights { get; set; }

    public DriveState Drives { get; } = new();

    public MemoryStore Memory { get; } = new();

    public required string PlaceId { get; set; }

    public CurrentAction? Current { get; set; }

    /// <summary>Where they are walking to, if the chosen action is somewhere else.</summary>
    public string? DestinationPlaceId { get; set; }

    public bool IsAlive { get; set; } = true;

    public double DiedOnDay { get; set; } = -1;

    /// <summary>The most recent thing Layer 2 had them think. Null until the fast lane runs in M3.</summary>
    public string? LastThought { get; set; }

    /// <summary>Model calls this mind has made. The inspector's Trace tab counts these.</summary>
    public int ModelCalls { get; set; }

    public int TokensSpent { get; set; }

    public double AgeInDays(double currentDay) => currentDay - BornOnDay;

    /// <summary>
    /// Mood is derived, never generated. A whole model call for one adjective is the wrong trade
    /// at any budget, so it comes off drive levels and the valence of what just happened.
    /// </summary>
    public string Mood(double currentDay)
    {
        var recentValence = Memory.Episodic
            .Where(m => currentDay - m.Day < 1.5)
            .Select(m => m.Valence * m.Intensity)
            .DefaultIfEmpty(0)
            .Average();

        var dominant = Drives.Dominant(Weights);
        var pressure = Drives.Urgency(dominant, Weights[dominant]);

        return (recentValence, pressure) switch
        {
            ( > 0.25, < 0.4) => "settled",
            ( > 0.25, _) => "driven",
            ( < -0.25, > 0.5) => "raw",
            ( < -0.25, _) => "sullen",
            (_, > 0.6) => "restless",
            (_, < 0.2) => "easy",
            _ => "guarded"
        };
    }
}
