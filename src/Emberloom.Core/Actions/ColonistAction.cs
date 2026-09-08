using Emberloom.Core.Drives;
using Emberloom.Core.World;

namespace Emberloom.Core.Actions;

public enum ActionKind
{
    Forage,
    Salvage,
    Clear,
    Build,
    Rest,
    Talk,
    Teach,
    Tend,
    Wander
}

/// <summary>
/// One thing a colonist can be doing. Actions declare which drives they relieve and where they
/// can happen; the scorer does the rest. Nothing here consults a model.
/// </summary>
public sealed record ActionDefinition
{
    public required ActionKind Kind { get; init; }

    public required string Verb { get; init; }

    /// <summary>Relief delivered per colony-day of work, before personality is applied.</summary>
    public required DriveVector Relief { get; init; }

    /// <summary>Where this can happen. Empty means anywhere.</summary>
    public IReadOnlyList<PlaceKind> Places { get; init; } = [];

    /// <summary>Typical ticks to complete. Shorter actions get re-evaluated more often.</summary>
    public required int DurationTicks { get; init; }

    public bool RequiresCompany { get; init; }

    /// <summary>Actions in dark or dangerous places aggravate Safety while they run.</summary>
    public bool ExposesToDanger { get; init; }

    public bool CanHappenAt(Place place) =>
        Places.Count == 0 || Places.Contains(place.Kind);
}

/// <summary>What a colonist is currently doing, and how far through it they are.</summary>
public sealed class CurrentAction
{
    public required ActionDefinition Definition { get; init; }

    public required Place Target { get; init; }

    public string? PartnerId { get; init; }

    public int TicksElapsed { get; set; }

    /// <summary>The score this action won on. Kept so hysteresis has something to compare against.</summary>
    public required double ChosenScore { get; init; }

    /// <summary>Ticks spent walking, before any work started. Travel is a cost, not an action.</summary>
    public int TravelTicks { get; set; }

    public bool IsComplete => TicksElapsed >= Definition.DurationTicks;

    public double Progress => Math.Clamp((double)TicksElapsed / Definition.DurationTicks, 0, 1);
}

public static class ActionCatalog
{
    /// <summary>
    /// The one balance knob for the whole body.
    /// <para>
    /// The seven decay rates accumulate 1.60 units of need per colony-day. The rates below, if a
    /// colonist worked every tick of every day, would supply about 2.75 — comfortably more than
    /// demand, which pins every drive at zero, flattens the scores, and leaves action selection
    /// to noise. Scaling supply to just under demand means a colonist working flat out roughly
    /// breaks even, and every tick spent travelling, sleeping or idle is a tick a drive climbs.
    /// </para>
    /// <para>
    /// Raise it and the colony coasts; lower it and they never look up from subsistence, which
    /// costs the setting the whole point of an abundant world.
    /// </para>
    /// </summary>
    public static double ReliefScale { get; set; } = 3.0;

    private static DriveVector Relief(params (Drive Drive, double Amount)[] entries) =>
        new(entries.ToDictionary(e => e.Drive, e => e.Amount));

    // Relief is a RATE, per colony-day spent doing the thing — not a lump sum awarded on
    // completion. Each primary relief sits at roughly 8–10x the drive's own decay rate from the
    // design's table, so about a ninth of a day's work covers a day's accumulation on that drive.
    //
    // Seven drives at a ninth of a day each is about 78% of the day, and travel eats the rest.
    // That is deliberate: the day very nearly holds everything a colonist needs to do, so
    // prioritising is a real trade-off rather than a queue everyone reaches the bottom of.

    public static readonly ActionDefinition Forage = new()
    {
        Kind = ActionKind.Forage,
        Verb = "foraged",
        Relief = Relief((Drive.Sustenance, 2.00), (Drive.Safety, 0.10)),
        Places = [PlaceKind.Clearing],
        DurationTicks = 26
    };

    public static readonly ActionDefinition Salvage = new()
    {
        Kind = ActionKind.Salvage,
        Verb = "worked the salvage at",
        Relief = Relief((Drive.Wonder, 2.60), (Drive.Meaning, 0.80), (Drive.Standing, 0.40)),
        Places = [PlaceKind.Ruin],
        DurationTicks = 34,
        ExposesToDanger = true
    };

    public static readonly ActionDefinition Clear = new()
    {
        Kind = ActionKind.Clear,
        Verb = "worked at clearing",
        Relief = Relief((Drive.Wonder, 1.80), (Drive.Meaning, 1.00), (Drive.Standing, 0.30)),
        Places = [PlaceKind.Stairwell, PlaceKind.Ruin],
        DurationTicks = 40,
        ExposesToDanger = true
    };

    public static readonly ActionDefinition Build = new()
    {
        Kind = ActionKind.Build,
        Verb = "built at",
        Relief = Relief((Drive.Meaning, 2.20), (Drive.Safety, 0.70), (Drive.Standing, 0.40)),
        Places = [PlaceKind.Shelter, PlaceKind.Plaza],
        DurationTicks = 32
    };

    public static readonly ActionDefinition Rest = new()
    {
        Kind = ActionKind.Rest,
        Verb = "slept at",
        Relief = Relief((Drive.Rest, 3.00), (Drive.Safety, 0.40)),
        Places = [PlaceKind.Shelter],
        DurationTicks = 48
    };

    public static readonly ActionDefinition Talk = new()
    {
        Kind = ActionKind.Talk,
        Verb = "talked with",
        Relief = Relief((Drive.Belonging, 2.20), (Drive.Standing, 0.40), (Drive.Meaning, 0.20)),
        DurationTicks = 12,
        RequiresCompany = true
    };

    public static readonly ActionDefinition Teach = new()
    {
        Kind = ActionKind.Teach,
        Verb = "taught",
        Relief = Relief((Drive.Meaning, 1.60), (Drive.Standing, 2.20), (Drive.Belonging, 0.50)),
        DurationTicks = 18,
        RequiresCompany = true
    };

    public static readonly ActionDefinition Tend = new()
    {
        Kind = ActionKind.Tend,
        Verb = "tended",
        // Tending the Loom is the safe route to Wonder: less rich than salvage, but nothing
        // overhead gives way. A careful colonist can live here; a bold one finds it thin.
        Relief = Relief((Drive.Meaning, 1.00), (Drive.Wonder, 1.70), (Drive.Safety, 0.30)),
        Places = [PlaceKind.Loom],
        DurationTicks = 24
    };

    public static readonly ActionDefinition Wander = new()
    {
        Kind = ActionKind.Wander,
        Verb = "wandered at",
        Relief = Relief((Drive.Wonder, 0.10), (Drive.Rest, 0.20), (Drive.Meaning, 0.10)),
        DurationTicks = 14
    };

    public static readonly IReadOnlyList<ActionDefinition> All =
        [Forage, Salvage, Clear, Build, Rest, Talk, Teach, Tend, Wander];
}
