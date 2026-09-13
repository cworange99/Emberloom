using Emberloom.Core.Culture;
using Emberloom.Core.Events;
using Emberloom.Core.Forks;
using Emberloom.Core.Life;
using Emberloom.Core.Relationships;
using Emberloom.Core.Verbs;
using Emberloom.Core.World;

namespace Emberloom.Core.Colonists;

/// <summary>
/// Everything that exists. The colony owns the map, the cast, the ledger and the feed; the
/// simulation owns the clock and drives this forward.
/// </summary>
public sealed class Colony
{
    /// <summary>
    /// A colony-day in ticks. One tick is roughly six minutes of colony time — fine enough that
    /// an action takes a believable while, coarse enough that a day is cheap to simulate.
    /// </summary>
    public const int TicksPerDay = 240;

    private readonly List<Colonist> _colonists = [];

    public Colony(CityMap map)
    {
        Map = map;
    }

    public CityMap Map { get; }

    public RelationshipLedger Relationships { get; } = new();

    public EventFeed Feed { get; } = new();

    public long Tick { get; set; }

    public double Day => (double)Tick / TicksPerDay;

    /// <summary>Fraction through the current day, 0 at dawn.</summary>
    public double TimeOfDay => (double)(Tick % TicksPerDay) / TicksPerDay;

    public bool IsNight => TimeOfDay is < 0.22 or > 0.80;

    /// <summary>
    /// The colony's scarcest resource, and the thing every genuine fork is ultimately about.
    /// Trickles in from the Loom; spent on printing and on the player's Provide verb.
    /// </summary>
    public double LoomCharge { get; set; } = 40;

    public double LoomChargePerDay { get; set; } = 3.2;

    public const double PrintCost = 25;

    /// <summary>Maker-artifacts recovered. Each is findable exactly once, anywhere in the city.</summary>
    public int ArtifactsRecovered { get; set; }

    /// <summary>The question the colony is deciding right now, if any. One at a time.</summary>
    public Fork? OpenFork { get; set; }

    /// <summary>Every fork decided so far, oldest first — the colony's turning points.</summary>
    public List<Fork> ForkHistory { get; } = [];

    /// <summary>
    /// Places the colony has not agreed to go. A floor above a newly cleared stairwell starts here
    /// and stays until a fork says otherwise; nobody's utility score reaches past it.
    /// </summary>
    public HashSet<string> ForbiddenPlaces { get; } = [];

    private readonly Dictionary<ForkKind, double> _askAgainOn = [];

    /// <summary>The day a question the colony said no to may be raised again.</summary>
    public double AskAgainOn(ForkKind kind) => _askAgainOn.GetValueOrDefault(kind);

    public void PutOff(ForkKind kind, double until) => _askAgainOn[kind] = until;

    /// <summary>Light and warmth the player keeps running on the Loom's charge, oldest first.</summary>
    public List<Provision> Provisions { get; } = [];

    public bool IsProvided(ProvisionKind kind, string placeId) =>
        Provisions.Any(p => p.Kind == kind && p.PlaceId == placeId);

    public bool IsLit(string placeId) => IsProvided(ProvisionKind.Light, placeId);

    public bool IsWarmed(string placeId) => IsProvided(ProvisionKind.Warmth, placeId);

    /// <summary>How light a place is, counting any light the player keeps burning there.</summary>
    public double LightAt(Place place) => IsLit(place.Id) ? 1.0 : place.Light;

    /// <summary>What the provisions cost, in charge per colony-day.</summary>
    public double UpkeepPerDay => Provisions.Sum(p => p.CostPerDay);

    /// <summary>
    /// Which of the makers' things have been shown, and to whom. Irreversible. The myth system
    /// (M7) reads this: what the colony has seen is what its founding story has to account for.
    /// </summary>
    public Dictionary<string, string> Revealed { get; } = [];

    /// <summary>Every dead mind's core, and what the colony decided to do with it.</summary>
    public List<MindCore> Cores { get; } = [];

    /// <summary>The colony's own words, oldest first. Each one goes into prompts verbatim.</summary>
    public List<CoinedWord> Lexicon { get; } = [];

    /// <summary>Every telling of the story the colony tells about its makers, oldest first.</summary>
    public List<MythTelling> MythHistory { get; } = [];

    /// <summary>The story as it is told now, if the colony has one yet.</summary>
    public MythTelling? Myth => MythHistory.Count == 0 ? null : MythHistory[^1];

    /// <summary>Occasions already offered for a word, so the same practice is not named twice.</summary>
    public HashSet<string> CoinedOccasions { get; } = [];

    public IReadOnlyList<Colonist> Colonists => _colonists;

    public IEnumerable<Colonist> Living => _colonists.Where(c => c.IsAlive);

    public IEnumerable<Colonist> ColonistsAt(string placeId) =>
        _colonists.Where(c => c.IsAlive && c.PlaceId == placeId);

    public Colonist? Find(string id) => _colonists.FirstOrDefault(c => c.Id == id);

    public void Add(Colonist colonist) => _colonists.Add(colonist);

    public void Log(
        EventLayer layer,
        string text,
        Colonist? colonist = null,
        string? placeId = null,
        bool notable = false,
        string? quote = null) =>
        Feed.Append(new ColonyEvent
        {
            Tick = Tick,
            Day = Day,
            Layer = layer,
            Text = text,
            ColonistId = colonist?.Id,
            PlaceId = placeId ?? colonist?.PlaceId,
            IsNotable = notable,
            Quote = quote
        });
}
