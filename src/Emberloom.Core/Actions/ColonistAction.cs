using Emberloom.Core.Drives;
using Emberloom.Core.Life;
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
    Wander,

    /// <summary>An evening at a hall the colony built, paid for out of what the work earned.</summary>
    Gather,

    /// <summary>Sitting on a council at a fork. Offered only to the council, and only while it sits.</summary>
    Deliberate
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

    /// <summary>
    /// What doing this costs the colonist in tokens, per time it is done — M13. Zero for everything
    /// the colony has always been able to do for nothing, which is everything else. A colonist who
    /// cannot pay is not offered it rather than being offered it and refused: see
    /// UtilityScorer.IsAvailable for why that is the version worth measuring first.
    /// </summary>
    /// <para>
    /// Settable rather than init-only for the same reason <see cref="ActionCatalog.ReliefScale"/>
    /// is: it is a balance knob whose right value is measured, and the measuring instrument has to
    /// be able to turn it. The game never writes it; only the price sweep does.
    /// </para>
    public double Price { get; set; }

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

    /// <summary>
    /// The definition for a kind. Deliberate is not in All — the scorer offers it only to a sitting
    /// council — but a saved colonist can be in the middle of one.
    /// </summary>
    public static ActionDefinition ByKind(ActionKind kind) =>
        All.Append(Deliberate).First(a => a.Kind == kind);

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

    /// <summary>
    /// Raising a building on ground the colony voted to raise it on. Until M10 this was offered at
    /// every shelter and plaza and did nothing whatsoever when it finished — a third of the
    /// colony's waking life, measured, spent on work that left no mark. It now happens only at an
    /// approved site, and when it is finished the site becomes a building.
    /// </summary>
    public static readonly ActionDefinition Build = new()
    {
        Kind = ActionKind.Build,
        Verb = "built at",
        Relief = Relief((Drive.Meaning, 2.20), (Drive.Safety, 0.70), (Drive.Standing, 0.40)),
        Places = [PlaceKind.Site],
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

    /// <summary>
    /// Sitting on a council at the plaza while a fork is open. Not in <see cref="All"/>: the scorer
    /// offers it only to the council, and only while the fork is undecided.
    /// </summary>
    public static readonly ActionDefinition Deliberate = new()
    {
        Kind = ActionKind.Deliberate,
        Verb = "argued at",
        Relief = Relief((Drive.Belonging, 1.20), (Drive.Standing, 1.40), (Drive.Meaning, 0.60)),
        Places = [PlaceKind.Plaza],
        DurationTicks = 30
    };

    /// <summary>
    /// An evening at a hall. The first thing in the colony that costs money, the first thing that
    /// needs a building the colony voted to raise, and the first real supplier of Standing.
    /// <para>
    /// Standing is the point. Talk already buys company and measures out at 4% of the day; what
    /// nothing supplies is being *seen* — Standing sat at 0.61 at M12, the highest of the seven,
    /// with only Teach reaching it and only in pairs. A hall is where a colonist is among everyone
    /// rather than beside one person, so the relief is weighted the other way round from Talk's.
    /// </para>
    /// <para>
    /// It does not require company, deliberately. Pairing an action off needs somebody already
    /// standing there, and nobody would ever be first. A hall pulls on everyone's score at once
    /// instead, and they arrive together because they all want the same thing at the same time.
    /// </para>
    /// </summary>
    public static readonly ActionDefinition Gather = new()
    {
        Kind = ActionKind.Gather,
        Verb = "spent the evening at",
        Relief = Relief((Drive.Standing, 2.40), (Drive.Belonging, 1.60), (Drive.Rest, 0.30), (Drive.Wonder, 0.20)),
        Places = [PlaceKind.Hall],
        DurationTicks = 20,
        Price = EconomyRules.GatheringPrice
    };

    public static readonly IReadOnlyList<ActionDefinition> All =
        [Forage, Salvage, Clear, Build, Rest, Talk, Teach, Tend, Wander, Gather];
}
