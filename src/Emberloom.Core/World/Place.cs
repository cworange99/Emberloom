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
    Plaza,

    /// <summary>
    /// Somewhere the colony can put a building. Until it votes to raise one, a site is marked
    /// ground and nothing else — see <see cref="Place.BuildRequired"/>.
    /// </summary>
    Site,

    /// <summary>A roof over tables. Where a colony stops working and sits with each other.</summary>
    Hall,

    /// <summary>Where what the colony grows is kept, against the days it does not grow anything.</summary>
    Granary,

    /// <summary>Beds, under a roof the colony built rather than found.</summary>
    Dwelling
}

/// <summary>
/// One location in the reclaimed city. The map is a graph rather than a grid — travel is a cost,
/// not a puzzle, and nothing here is ever worth a model call.
/// </summary>
public sealed class Place
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// How many people can sleep here — M15. Derived from what the place is rather than stored, so
    /// a site that becomes a dwelling gains its beds the moment it stands and a save needs no new
    /// field for it.
    /// <para>
    /// Two found shelters at three apiece is room for six, which is comfortable for four founders
    /// and runs out at about the first generation. That is the point: a colony that grows has to
    /// build somewhere for the people it grew, and "there was nowhere to sleep" is visible in the
    /// feed in a way that a job queue never would be.
    /// </para>
    /// </summary>
    public int Beds => Kind switch
    {
        PlaceKind.Shelter => 3,
        PlaceKind.Dwelling => 4,
        _ => 0
    };

    /// <summary>
    /// What this place is. Settable since M10 only because a site becomes the thing it was raised
    /// as: the ground where the colony voted for a hall is a Site until the work is done and a Hall
    /// afterwards, and that is one place changing rather than two places sharing a spot.
    /// </summary>
    public required PlaceKind Kind { get; set; }

    /// <summary>
    /// What this place will be once it is built, for a <see cref="PlaceKind.Site"/>. Null for
    /// anything the makers left behind — those were never raised by anybody here.
    /// </summary>
    public PlaceKind? Becomes { get; init; }

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

    /// <summary>
    /// Work ticks needed to raise a building here. Zero for everywhere the makers left — those are
    /// cleared, not built. Mirrors <see cref="ClearingRequired"/> deliberately: clearing is how the
    /// colony gets into what was already there, building is how it adds something that was not.
    /// </summary>
    public double BuildRequired { get; set; }

    public double BuildDone { get; set; }

    /// <summary>
    /// True once the colony has voted to raise this site, which is what takes it out of
    /// <see cref="Colonists.Colony.ForbiddenPlaces"/> and lets anyone work on it. A site nobody has
    /// agreed to is marked ground that everyone walks past.
    /// </summary>
    public bool IsApproved { get; set; }

    public List<string> Neighbours { get; } = [];

    public bool IsBlocked => ClearingRequired > ClearingDone;

    /// <summary>
    /// Still going up. A site under construction is not yet the thing it will be: it holds nobody,
    /// feeds nobody, and relieves nothing.
    /// </summary>
    public bool IsUnderConstruction => BuildRequired > BuildDone;

    public double BuildProgress =>
        BuildRequired <= 0 ? 1 : Math.Clamp(BuildDone / BuildRequired, 0, 1);

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
