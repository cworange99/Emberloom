namespace Emberloom.Core.World;

public enum PlaceKind
{
    /// <summary>Green, open, generous. Where foraging happens.</summary>
    Clearing,

    /// <summary>A building the forest has partly taken. Salvage, and the floors above it.</summary>
    Ruin,

    /// <summary>Claimed and roofed. The only place Rest is properly relieved.</summary>
    Shelter,

    /// <summary>A way up or in that has not been cleared yet. Wonder's target.</summary>
    Stairwell,

    /// <summary>The machine. Still printing, still on trickle charge.</summary>
    Loom,

    /// <summary>Open ground the colony gathers on. Where arguments happen.</summary>
    Plaza
}

/// <summary>
/// One location in the reclaimed city. The map is a graph rather than a grid — travel is a cost,
/// not a puzzle, and nothing here is ever worth a model call.
/// </summary>
public sealed class Place
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required PlaceKind Kind { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    /// <summary>Which floor this sits on. Higher floors are further from anything anyone understands.</summary>
    public int Level { get; init; }

    /// <summary>0–1. Unstable ruins, live machinery, dark levels. Feeds the Safety drive directly.</summary>
    public double Danger { get; init; }

    /// <summary>0–1. Dark places aggravate Safety and cannot be worked at night.</summary>
    public double Light { get; init; } = 1.0;

    /// <summary>Forage yield per tick of work, as Sustenance relief. The forest is generous.</summary>
    public double ForageYield { get; init; }

    /// <summary>
    /// Maker-artifacts remaining here. Each is findable exactly once, which is what makes salvage
    /// archaeology rather than a resource node.
    /// </summary>
    public int SalvageRemaining { get; set; }

    /// <summary>False until someone has cleared or reached it. Unexplored places drive Wonder.</summary>
    public bool IsExplored { get; set; }

    /// <summary>Work ticks still needed to open this place up. Stairwells start blocked.</summary>
    public double ClearingRequired { get; set; }

    public double ClearingDone { get; set; }

    public List<string> Neighbours { get; } = [];

    public bool IsBlocked => ClearingRequired > ClearingDone;

    public double ClearingProgress =>
        ClearingRequired <= 0 ? 1 : Math.Clamp(ClearingDone / ClearingRequired, 0, 1);

    public double DistanceTo(Place other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        var dz = (Level - other.Level) * 12.0; // stairs cost more than ground
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
