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

    /// <summary>
    /// What this colony was grown from. Kept so that anything derived rather than drawn — names,
    /// so far — can vary between colonies without taking numbers out of the random stream, which
    /// would reshuffle everything downstream of wherever it was taken.
    /// </summary>
    public int Seed { get; set; }

    /// <summary>
    /// How far the player may reach into this colony. Set when it is created and never afterwards:
    /// a run that was observed for half its life and steered for the other half is evidence of
    /// nothing. Defaults to <see cref="ColonyMode.Hybrid"/>, which is what v1 shipped.
    /// </summary>
    public ColonyMode Mode { get; init; } = ColonyMode.Hybrid;

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

    /// <summary>
    /// What the Loom makes on its own, per colony-day — **nothing, from M14**.
    /// <para>
    /// Through v1 this was a flat trickle of about 0.06 a day whatever anybody did, which is why the
    /// Print fork had no argument in it: the council was not deciding whether the colony could
    /// afford another mouth, it was waiting for a threshold to cross on its own. Measured over 2160
    /// days on the shipped seed, the colony reached 57.8 charge without a single person having
    /// earned any of it. Labour feeds the Loom now — see <see cref="ChargePerWorkDay"/> — and this
    /// is what is left over for anything that changes the machine itself, which is the
    /// <c>charge-lines</c> revelation and nothing else.
    /// </para>
    /// </summary>
    public double LoomChargePerDay { get; set; }

    /// <summary>
    /// What one colonist-day of paid work puts into the Loom. The colony's single stock: the scope's
    /// separate food and grain store is this, because a colony with three resources to track is a
    /// spreadsheet and a colony with one has an argument.
    /// <para>
    /// Only grown colonists feed it. Someone younger than <see cref="LifeRules.MaturityDays"/> is
    /// fed by the colony and gives nothing back yet, which is what makes "can we feed another?" a
    /// real question rather than an accounting one — a print or a birth costs for a third of a
    /// lifetime before it starts paying.
    /// </para>
    /// </summary>
    public double ChargePerWorkDay { get; set; } = 0.5;

    /// <summary>
    /// What each living colonist takes from the stores a day, grown or not. Measured against the
    /// colony's real working day: see DESIGN-V2.md for the shape this produces, which is a small
    /// colony that accumulates, a middling one that holds, and a large one that slowly cannot.
    /// </summary>
    public double DrawPerHeadPerDay { get; set; } = 0.16;

    /// <summary>What the living take from the stores a day, before anything the player keeps running.</summary>
    public double DrawPerDay => Living.Count() * DrawPerHeadPerDay;

    /// <summary>What the Loom's own cells hold, with nowhere else to put anything.</summary>
    public const double BaseStoreCapacity = 60;

    /// <summary>What one raised granary adds to what the colony can keep.</summary>
    public const double StorePerGranary = 120;

    /// <summary>
    /// How much the colony can actually keep — M14. Surplus past this is not kept, because there is
    /// nowhere to put it.
    /// <para>
    /// This is what a granary is *for*, and the reason it needed one. Measured with labour feeding
    /// the Loom and no ceiling at all, a settled colony of twenty adults reached 2,592 charge, eight
    /// hundred days of stores, and the Print fork's resource argument became vacuous again from the
    /// other direction. A ceiling that the colony can raise by building also turns the Granary vote
    /// — the one <see cref="ForkRules"/> deliberately gives the least pressure, because nothing
    /// obliges anyone to want one — into a question with a real answer behind it.
    /// </para>
    /// </summary>
    public double StoreCapacity =>
        BaseStoreCapacity + Map.Places.Count(p => p.Kind == PlaceKind.Granary) * StorePerGranary;

    /// <summary>How full the stores are, 0 to 1. What a colonist would notice without counting.</summary>
    public double StoresFull => StoreCapacity <= 0 ? 1 : Math.Clamp(LoomCharge / StoreCapacity, 0, 1);

    /// <summary>True while surplus is being lost for want of anywhere to keep it.</summary>
    public bool StoresOverflowing { get; set; }

    /// <summary>
    /// What the colony actually put into the Loom on the last full day. Measured rather than
    /// derived, because it depends on what everybody chose to do.
    /// </summary>
    public double ProducedPerDay { get; set; }

    /// <summary>Running total for the day in progress; rolled into <see cref="ProducedPerDay"/> at midnight.</summary>
    public double ProducingToday { get; set; }

    /// <summary>
    /// Charge in, charge out. Negative means the colony is living off what it has.
    /// <para>
    /// Flat until a day's work has actually been measured. A colony reads nothing but its draw on
    /// the morning it is printed, and without this every brand-new colony — and every test that
    /// sets the clock forward rather than living through the days — looks like it is starving.
    /// Unknown is not the same as falling.
    /// </para>
    /// </summary>
    public double ChargeFlowPerDay =>
        ProducedPerDay <= 0 ? 0 : ProducedPerDay + LoomChargePerDay - DrawPerDay - UpkeepPerDay;

    /// <summary>
    /// How many colony-days the stores would last if everybody stopped working — the figure the
    /// Print fork is really about. Infinite when the colony takes nothing, which only happens when
    /// everybody is dead.
    /// </summary>
    public double DaysOfStores => DrawPerDay + UpkeepPerDay <= 0
        ? double.PositiveInfinity
        : LoomCharge / (DrawPerDay + UpkeepPerDay);

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

    /// <summary>
    /// Ground the colony has voted not to raise, and the day it is willing to hear about it again.
    /// A no moves the next Build fork on to different ground — otherwise the colony would be asked
    /// the same question every sixty days until it said yes, which is not deciding anything.
    /// <para>
    /// It expires rather than standing for ever. A colony running with no mind at all votes by
    /// comparing personality weights, and four founders who all lean the same way could otherwise
    /// refuse every site in the city once and be locked out of building forever.
    /// </para>
    /// </summary>
    public Dictionary<string, double> Declined { get; } = [];

    /// <summary>Whether this ground is currently set aside.</summary>
    public bool IsDeclined(string placeId) =>
        Declined.TryGetValue(placeId, out var until) && Day < until;

    private readonly Dictionary<ForkKind, double> _askAgainOn = [];

    /// <summary>The day a question the colony said no to may be raised again.</summary>
    public double AskAgainOn(ForkKind kind) => _askAgainOn.GetValueOrDefault(kind);

    public void PutOff(ForkKind kind, double until) => _askAgainOn[kind] = until;

    /// <summary>Every question currently put off, and until when. For saving the colony.</summary>
    public IReadOnlyDictionary<ForkKind, double> AskAgainEntries => _askAgainOn;

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
