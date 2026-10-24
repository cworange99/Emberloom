using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Events;
using Emberloom.Core.Life;
using Emberloom.Core.World;

namespace Emberloom.Core.Forks;

/// <summary>The questions a colony can put to itself. Each has a consequence the body applies when it is decided.</summary>
public enum ForkKind
{
    /// <summary>The Loom has charge for another mind. Spend it, or keep it?</summary>
    Print,

    /// <summary>A way up has been cleared. Go further, or leave what is up there alone?</summary>
    Ascend,

    /// <summary>Someone has died, and their mind is still in its core. Read it, keep it, or destroy it?</summary>
    Core,

    /// <summary>
    /// There is ground to build on. Raise this one next, or leave it and be asked about another?
    /// <para>
    /// Deliberately binary, like every other fork. "What do we build next?" is the question the
    /// colony is really answering, but it answers it as a sequence of yes-or-no questions about one
    /// site at a time: a no puts that site aside and the body proposes the next-best one. A genuine
    /// multiple-choice fork would mean generalising Stance, the council's two-drive axis, the
    /// fallback vote and a deliberation schema whose three options the model is used to — for the
    /// same outcome. See DESIGN-V2.md, Deferred.
    /// </para>
    /// </summary>
    Build
}

public enum Stance
{
    For,
    Against,
    Abstain
}

/// <summary>One participant's say at a fork: where they stand, how firmly, and what they said aloud.</summary>
public sealed record Vote(Stance Stance, double Conviction, string Line, string Reasoning, bool WasFallback)
{
    /// <summary>The call that wrote this vote, when a mind did. Lets the inspector open it.</summary>
    public string? CallId { get; init; }
}

/// <summary>
/// A genuine fork: a question the whole colony has a stake in, decided by a council's votes rather
/// than by anyone's utility score. The body flags it, the mind writes each participant's stance, and
/// the body tallies them and applies the outcome. Nothing about the decision is authored.
/// </summary>
public sealed class Fork
{
    private readonly Dictionary<string, Vote> _votes = [];
    private readonly List<string> _order = [];

    public required string Id { get; init; }

    public required ForkKind Kind { get; init; }

    /// <summary>The question as the colony puts it to itself.</summary>
    public required string Question { get; init; }

    /// <summary>What agreeing means, concretely — what happens if the vote carries.</summary>
    public required string Proposal { get; init; }

    /// <summary>
    /// What is at stake, in words. The deliberation prompt reads this, and a model hands raw figures
    /// straight back as convictions, so there are none in it.
    /// </summary>
    public required string Stakes { get; init; }

    /// <summary>
    /// The same thing <see cref="Stakes"/> says, as a number in [0,1]: how badly the colony needs
    /// what is proposed, measured when the question was asked.
    /// <para>
    /// There are two readers of a fork and only one of them can read prose. A mind is given the
    /// stakes in words, because figures in a prompt come back as beliefs; a colony with no mind
    /// votes in <see cref="ForkRules.FallbackVote"/>, which until now read nothing but personality
    /// and so answered every question the same way for ever. Measured at M12: on seed 4 — the seed
    /// the game ships with — a council of three who all rest easy refused all twelve sites twenty
    /// times over 600 days, and would have refused them for ever, because nothing in the vote could
    /// tell them that eleven people were sharing two beds. This is how that fact reaches them.
    /// </para>
    /// </summary>
    public double Pressure { get; init; }

    /// <summary>For an Ascend fork, the place the colony is deciding whether to go up to.</summary>
    public string? SubjectPlaceId { get; init; }

    /// <summary>For a Core fork, whose core it is.</summary>
    public string? SubjectColonistId { get; init; }

    /// <summary>
    /// What each stance means for this question, in the words the deliberation prompt uses. For
    /// most forks "against" just means not doing it; for a core it means destroying it, and a
    /// council that is not told that will argue the wrong question.
    /// </summary>
    public string ForMeans { get; init; } = "they want it done";

    public string AgainstMeans { get; init; } = "they do not want it done, even gently";

    public string AbstainMeans { get; init; } = "they truly have no preference";

    /// <summary>Where the council gathers. The plaza: where arguments happen.</summary>
    public required string PlaceId { get; init; }

    public required double OpenedOnDay { get; init; }

    /// <summary>The council, in the order they are asked. Later speakers hear what earlier ones said.</summary>
    public required IReadOnlyList<string> Participants { get; init; }

    /// <summary>
    /// How many council members' minds are still working on their say. While any are, the fork
    /// waits for them however long that takes: the timeout is counted in colony days, and at 60x a
    /// colony day passes in seconds — long before a deep call on the laptop comes back. The timeout
    /// is for a colony with no mind at all, where nothing is ever pending.
    /// </summary>
    public int AwaitingMinds { get; set; }

    public IReadOnlyDictionary<string, Vote> Votes => _votes;

    /// <summary>Votes in the order they were cast — the order the argument actually happened in.</summary>
    public IEnumerable<(string ColonistId, Vote Vote)> Spoken => _order.Select(id => (id, _votes[id]));

    /// <summary>Null while the colony is still deciding.</summary>
    public bool? Carried { get; private set; }

    /// <summary>
    /// The side that got its way, once decided. Null when neither did — which only a core can
    /// end in: no majority to read it and none to destroy it keeps it, unread.
    /// </summary>
    public Stance? Prevailed { get; private set; }

    /// <summary>What was decided, in a word or two: "yes", "no", or a core's fate.</summary>
    public string? Outcome { get; private set; }

    public double? ResolvedOnDay { get; private set; }

    public bool IsOpen => Carried is null;

    public bool AllVoted => Participants.All(_votes.ContainsKey);

    /// <summary>
    /// Records a vote. A vote arriving after the fork was decided is refused — the colony has moved
    /// on, and a turning point does not get re-run because someone's mind was slow.
    /// </summary>
    public bool Cast(string colonistId, Vote vote)
    {
        if (!IsOpen || !Participants.Contains(colonistId) || _votes.ContainsKey(colonistId))
        {
            return false;
        }

        _votes[colonistId] = vote;
        _order.Add(colonistId);
        return true;
    }

    public int Count(Stance stance) => _votes.Values.Count(v => v.Stance == stance);

    /// <summary>How many of the council must vote for it: more than half, whoever abstains.</summary>
    public int Needed => Participants.Count / 2 + 1;

    /// <summary>
    /// A majority of the whole council, by heads. It used to be conviction-weighted, for against
    /// against — and live at M5 one "for" and four abstentions printed a mind. Changing what the
    /// colony does takes most of the people deciding it; abstaining keeps things as they are.
    /// Conviction still colours what each member remembers, and shows on the banner.
    /// </summary>
    public bool HasMajority => Count(Stance.For) >= Needed;

    /// <summary>A majority against, by heads. For a core, that destroys it.</summary>
    public bool HasMajorityAgainst => Count(Stance.Against) >= Needed;

    internal void Decide(bool carried, Stance? prevailed, string outcome, double day)
    {
        Carried = carried;
        Prevailed = prevailed;
        Outcome = outcome;
        ResolvedOnDay = day;
    }
}

/// <summary>
/// When forks open, who sits on the council, and where a colonist stands when no mind is there to
/// say. All deterministic, all body — the one call a fork costs is the stance each participant's
/// mind writes, and the colony still decides without it.
/// </summary>
public static class ForkRules
{
    /// <summary>
    /// Five, not the whole colony. Each seat is one deep call, and on the laptop a deep call is most
    /// of a minute — eight of them would hold the colony at a standstill for a quarter of an hour.
    /// </summary>
    public const int CouncilSize = 5;

    /// <summary>
    /// What the Loom must hold before a print is worth arguing about — **what a print costs, plus two
    /// months of feeding one more mouth than there are now.**
    /// <para>
    /// It was a flat 45 until M14, which made sense in a world where the charge arrived on its own
    /// and nobody ate: the colony just wanted something left over for light and doors. Now that the
    /// living draw on the same stock, what is left over has to feed somebody, and how much that is
    /// depends on how many of them there already are. At four founders a print needs about 73
    /// charge; at twenty, about 250. That is the resource argument the Print fork never had, and
    /// most of it is arithmetic rather than politics — see <see cref="CanFeedAnother"/> for the
    /// measurement that made that the right division of labour.
    /// </para>
    /// </summary>
    public static double ChargeNeededToPrint(Colony colony) =>
        Colony.PrintCost + PrintReserveDays * DrawWithOneMore(colony);

    /// <summary>What the colony would take from the stores a day with one more mouth in it.</summary>
    private static double DrawWithOneMore(Colony colony) =>
        (colony.Living.Count() + 1) * colony.DrawPerHeadPerDay + colony.UpkeepPerDay;

    /// <summary>No forks before the colony has had time to become anyone. Kindling finishes first.</summary>
    public const double EarliestDay = 2.0;

    /// <summary>
    /// How long a fork with no minds working on it waits before deciding on where everyone stands.
    /// Only a colony with no mind at all ever reaches it; see <see cref="Fork.AwaitingMinds"/>.
    /// </summary>
    public const double TimeoutDays = 3.0;

    /// <summary>
    /// A question the colony said no to comes back, but not the next morning: about five real days
    /// at 1×. It was 6 colony days while a day was two minutes — a "no" that held for twelve.
    /// </summary>
    public const double PrintAskAgainDays = 120;

    public const double AscendAskAgainDays = 160;

    /// <summary>
    /// A building is the question the colony comes back to most, so the wait after a no is the
    /// shortest — and a no is about *that site*, which is put aside for good, not about building.
    /// </summary>
    public const double BuildAskAgainDays = 60;

    /// <summary>How long a particular piece of ground stays set aside after a no. See Colony.Declined.</summary>
    public const double GroundSetAsideDays = 400;

    /// <summary>
    /// The fork the colony should face now, if any. Only one at a time; a way up, when there is one,
    /// comes before a print — it is the rarer question and the bigger one.
    /// </summary>
    public static Fork? Detect(Colony colony)
    {
        if (colony.OpenFork is not null || colony.Day < EarliestDay || colony.Living.Count() < 2)
        {
            return null;
        }

        // Live at M5 the laptop was still writing the colony's first minds at day two, and the
        // first fork was argued by colonists with no personalities yet. Wait for them.
        if (colony.Living.Any(c => c.AwaitingKindling))
        {
            return null;
        }

        // The dead come first. A core waiting to be decided is the most personal question a
        // colony faces, and it should not queue behind a print.
        var waiting = colony.Cores.FirstOrDefault(c => c.Fate == CoreFate.Waiting);
        if (waiting is not null && colony.Find(waiting.ColonistId) is { } dead)
        {
            return OpenCore(colony, dead);
        }

        var above = colony.ForbiddenPlaces
            .Select(colony.Map.Find)
            .FirstOrDefault(p => p is not null);

        if (above is not null && colony.Day >= colony.AskAgainOn(ForkKind.Ascend))
        {
            return Open(colony, ForkKind.Ascend,
                question: $"Do we go up to {above.Name}?",
                proposal: $"Anyone may go up to {above.Name} and start clearing it.",
                stakes: $"The way up is open. Nobody has been to {above.Name}, and the stairwell below it has already been dangerous. Whatever the makers left up there, they left it high and hard to reach.",
                subject: above.Id);
        }

        // The cap belongs here rather than at the print itself: a body that asks a question whose
        // answer it would then refuse to honour is worse than one that does not ask. Births have
        // always been capped in ConsiderPairings and prints were not, which went unnoticed while the
        // Print fork almost never carried — with labour feeding the Loom it carries often, and three
        // seeds measured at M14 ran to 29, 41 and 27 living before this line existed. See
        // DESIGN-V2.md: the cap is really a thinking budget, and whether to raise it is open.
        if (colony.Living.Count() < LifeRules.PopulationCap
            && CanFeedAnother(colony)
            && colony.Day >= colony.AskAgainOn(ForkKind.Print))
        {
            return Open(colony, ForkKind.Print,
                question: "Do we let the Loom print another mind?",
                proposal: "The Loom spends its charge on one more mind.",
                stakes: Stores(colony),
                pressure: Plenty(colony),
                subject: null);
        }

        // Last, and most often: what to raise next. It comes after the others because it is the
        // question the colony will face again and again, and a core or a way up should never queue
        // behind it.
        if (colony.Day >= colony.AskAgainOn(ForkKind.Build)
            && !colony.Map.Places.Any(p => p.Kind == PlaceKind.Site && p.IsApproved)
            && Propose(colony) is { } site)
        {
            return Open(colony, ForkKind.Build,
                question: $"Do we raise {site.Name} next?",
                proposal: $"The colony builds {site.Name}. Anyone may work on it until it stands.",
                stakes: Stakes(colony, site),
                subject: site.Id,
                pressure: Pressure(colony, site),
                forMeans: "they want this one raised next",
                againstMeans: "they would leave this ground as it is, for now",
                abstainMeans: "they do not mind either way which is raised next");
        }

        return null;
    }

    /// <summary>
    /// Which site the colony is asked about. A deterministic body rule, not a vote: the colony
    /// chooses *whether*, and the body chooses *which* by what is most obviously short. Beds first
    /// while people are sleeping in what the makers left, then somewhere to sit together, then
    /// somewhere to put what is grown.
    /// <para>
    /// A site the colony has already said no to is put aside by <c>AskAgainOn</c>, and the next
    /// detection proposes the next one down this order — which is how a sequence of yes-or-no
    /// questions comes to the same place as "what shall we build next".
    /// </para>
    /// </summary>
    public static Place? Propose(Colony colony)
    {
        var sites = colony.Map.Places
            .Where(p => p.Kind == PlaceKind.Site && !p.IsApproved && !colony.IsDeclined(p.Id))
            .ToList();

        if (sites.Count == 0)
        {
            return null;
        }

        var heads = colony.Living.Count();
        var beds = colony.Map.Places.Count(p => p.Kind is PlaceKind.Shelter or PlaceKind.Dwelling);
        var halls = colony.Map.Places.Count(p => p.Kind == PlaceKind.Hall);

        var wanted = heads > beds * 3 ? PlaceKind.Dwelling
            : halls == 0 ? PlaceKind.Hall
            : heads > halls * 8 ? PlaceKind.Hall
            : PlaceKind.Granary;

        // Whatever is wanted, nearest the plaza first, so the city grows outward from where the
        // colony already gathers rather than scattering.
        return sites
            .Where(p => p.Becomes == wanted)
            .Concat(sites)
            .OrderBy(p => colony.Map.Find("plaza") is { } plaza ? p.DistanceTo(plaza) : 0)
            .First();
    }

    private static string Stakes(Colony colony, Place site)
    {
        var heads = colony.Living.Count();
        var beds = colony.Map.Places.Count(p => p.Kind is PlaceKind.Shelter or PlaceKind.Dwelling);

        var need = site.Becomes switch
        {
            PlaceKind.Dwelling => $"There are {heads} of us and {beds} places to sleep.",
            PlaceKind.Hall => "There is nowhere any of us go that is not somewhere we work.",
            PlaceKind.Granary => "Everything any of us gathers is carried and eaten the same day.",
            _ => "The ground is there."
        };

        // Both sides as sides, in words — the M5 lesson. No figures the model can hand back as a
        // belief, beyond the plain count of people and beds, which a colonist would know.
        return $"{need} For: it would be the first thing here the colony made rather than found, and it would stand after "
               + "whoever raised it. Against: it is a great deal of work that nobody has to do, and the ruins have held so far.";
    }

    /// <summary>How much must be left after a print, in colony-days of stores. Two months.</summary>
    public const double PrintReserveDays = 60;

    /// <summary>
    /// Whether the colony can feed another mouth — **a body-side gate, and it is here because the
    /// mind would not do it.**
    /// <para>
    /// M14's riskiest assumption was that a council told the stores were thin would vote against a
    /// print. It was measured before the milestone was built, with three worded variants of the
    /// Print stakes and thirty samples each against llama3.2:3b, and it is false. Thin stores
    /// produced *more* for-votes than adequate ones (15, 10, 17 out of 30 for thin, adequate and
    /// plentiful) and against was 2, 1 and 0 — the model essentially will not oppose a print at all.
    /// It is the M5 lesson again: whatever is at stake, a 3B model reads it as a reason to act.
    /// </para>
    /// <para>
    /// The one real signal was indirect: an adequate store produced nineteen abstentions against
    /// thirteen, and conviction fell from 0.50 to 0.30, so the model does register that a middling
    /// stock is a reason to be unsure. That is worth keeping the wording for, and it is nowhere near
    /// enough to run an economy on. So the stores constrain the colony here, in arithmetic, and the
    /// council argues about whether it *wants* another mind rather than whether it can afford one.
    /// </para>
    /// </summary>
    public static bool CanFeedAnother(Colony colony) =>
        colony.LoomCharge >= ChargeNeededToPrint(colony)

        // And either the colony is still gaining, or it has enough behind it not to care that it
        // isn't. One or the other: a colony living off its stores should not be adding to the
        // number of people living off them.
        && (colony.ChargeFlowPerDay >= 0 || colony.StoresFull > 0.5);

    /// <summary>
    /// What the stores look like, in words — M14, and the reason the Print fork now has an argument
    /// in it. Through v1 the charge arrived on its own at a fixed rate, so the council was never
    /// deciding whether the colony could afford another mouth; it was waiting for a number to cross
    /// a line by itself.
    /// <para>
    /// No figures. CLAUDE.md is explicit that a model reads every number in a prompt as something
    /// the colonist knows and hands it straight back as a conviction, and the M5 lesson here is
    /// sharper still: a looser version of the Against clause below was read as a reason *to* print.
    /// The clauses are therefore left exactly as they were measured working, and only the first
    /// sentence moves.
    /// </para>
    /// </summary>
    private static string Stores(Colony colony)
    {
        // Read off how full the bins are rather than off a count of days, because that is what a
        // colonist standing in front of them would see, and because what the colony can keep at all
        // depends on how many granaries it has raised.
        var reserve = colony.StoresFull switch
        {
            < 0.25 => "The stores are nearly out — what is gathered is eaten, and there is little behind it.",
            < 0.70 => "The stores hold. There is enough for everyone here, with something behind it.",
            _ => "The stores are as full as they will go — more is gathered than anyone eats."
        };

        var trend = colony.ChargeFlowPerDay < -0.01
            ? " And they are going down, not up."
            : colony.ChargeFlowPerDay > 0.01 ? " And they are still rising." : string.Empty;

        return $"{reserve}{trend} For: a print adds one more mind to the colony — more company, more hands. "
               + "Against: the charge is spent and gone; kept, it could light the dark places or open a door that will not open.";
    }

    /// <summary>
    /// The same thing as a number, for the council that cannot read — and the first pressure in the
    /// game that can be **negative**, because a thin store is an argument against the question
    /// rather than a weak argument for it. Everything else a colony votes on is a thing it either
    /// needs or merely could have; a print it cannot feed is worse than no print.
    /// </summary>
    private static double Plenty(Colony colony)
    {
        // Half-full is the point of indifference: below it the colony is plainly against feeding
        // another mouth, above it plainly for, and the ends of the range are reachable because the
        // stores have a ceiling the colony can raise by building.
        var reserve = Math.Clamp((colony.StoresFull - 0.5) * 2, -1, 1);

        // And which way they are moving, which is what makes a council that said no last season
        // able to say yes this one without anybody's personality having changed.
        var trend = Math.Clamp(colony.ChargeFlowPerDay * 2, -0.5, 0.5);

        return Math.Clamp(reserve + trend, -1, 1);
    }

    /// <summary>
    /// How short the colony actually is of what this site would become, in [0,1] — the same facts
    /// <see cref="Stakes"/> states in words, for the voter who cannot read them. Deliberately the
    /// same thresholds <see cref="Propose"/> uses to pick the site, so the thing the body says is
    /// most needed is also the thing a mindless council feels most strongly about.
    /// </summary>
    public static double Pressure(Colony colony, Place site)
    {
        var heads = colony.Living.Count();
        var beds = colony.Map.Places.Count(p => p.Kind is PlaceKind.Shelter or PlaceKind.Dwelling);
        var halls = colony.Map.Places.Count(p => p.Kind == PlaceKind.Hall);

        return site.Becomes switch
        {
            // Three to a room is crowded but liveable; past that it presses, and at six to a room
            // it is the only thing anyone would vote for.
            PlaceKind.Dwelling => Math.Clamp(heads / (beds * 3.0) - 1, 0, 1),

            // Having nowhere to be together that is not somewhere you work is a standing complaint
            // rather than an emergency, so the first hall presses moderately and never more.
            PlaceKind.Hall => halls == 0 ? 0.5 : Math.Clamp(heads / (halls * 8.0) - 1, 0, 0.5),

            // A store was pure foresight until M14 gave the colony something to keep. Watching
            // your own work spoil for want of anywhere to put it presses hard; bins merely filling
            // up presses the way foresight should, which is gently enough that the restless and the
            // proud raise one and nobody else has to. Beds are not negotiable, a hall is agreed to,
            // a store is argued over.
            PlaceKind.Granary => colony.StoresOverflowing
                ? 0.7
                : Math.Clamp((colony.StoresFull - 0.6) / 0.4, 0, 0.5),
            _ => 0
        };
    }

    /// <summary>
    /// A stairwell has just been cleared. Whatever is above it waits for the colony to decide: going
    /// up is the kind of thing a colony argues about, and nobody's utility score should settle it by
    /// wandering up there first.
    /// </summary>
    public static IReadOnlyList<Place> HoldTheWayUp(Colony colony, Place opened)
    {
        if (opened.Kind != PlaceKind.Stairwell)
        {
            return [];
        }

        var held = new List<Place>();

        foreach (var id in opened.Neighbours)
        {
            if (colony.Map.Find(id) is { } above && above.Level > opened.Level && colony.ForbiddenPlaces.Add(above.Id))
            {
                held.Add(above);
                colony.Log(EventLayer.World,
                    $"The way up to {above.Name} is open. Nobody goes further until the colony decides.",
                    placeId: opened.Id, notable: true);
            }
        }

        return held;
    }

    /// <summary>
    /// The two drives a fork pulls on — the one that argues for it and the one that argues against.
    /// A print is company against caution; a way up is curiosity against caution.
    /// </summary>
    public static (Drive For, Drive Against) Pulls(ForkKind kind) => kind switch
    {
        ForkKind.Print => (Drive.Belonging, Drive.Safety),
        ForkKind.Ascend => (Drive.Wonder, Drive.Safety),

        // Work that lasts, against the comfort of leaving things as they are — and Meaning is
        // exactly what Build relieves, so the people who argue for it are the people who will do it.
        //
        // Standing was the first choice and it was wrong, measurably: of the five archetypes only
        // three lean Standing over Rest, and the council seats whoever is furthest from ordinary on
        // the axis, so a small colony could seat two of the two who lean against and refuse every
        // building in the city for its whole life. On Meaning against Rest, four of five lean
        // towards raising something, which is the right baseline for a colony that builds.
        ForkKind.Build => (Drive.Meaning, Drive.Rest),
        _ => (Drive.Meaning, Drive.Safety)
    };

    /// <summary>
    /// Whoever feels it most, either way: the colonists whose weights on the two drives sit furthest
    /// from ordinary. A council of the indifferent does not argue, and an argument is the point.
    /// </summary>
    /// <summary>
    /// How many seats this colony can fill — always an odd number, so a council can always decide.
    /// <para>
    /// Found the hard way at M10, when the starting population dropped to four. Four living
    /// colonists seated a council of four, and because a stub vote is a function of personality
    /// weights that never change, the same four people split 2–2 on every question for their entire
    /// lives: seed 5 refused every building, every print and every stairwell for 600 days running.
    /// An odd council cannot deadlock.
    /// </para>
    /// </summary>
    public static int Seats(Colony colony)
    {
        var willing = Math.Min(CouncilSize, colony.Living.Count());
        return willing % 2 == 0 ? willing - 1 : willing;
    }

    /// <summary>
    /// Who sits: the person who feels it most strongly each way, and then whoever is most ordinary
    /// about it to decide between them.
    /// <para>
    /// It used to be simply the furthest from ordinary on either of the fork's two drives, and that
    /// turned out to be a quiet bias toward whichever side of the question has the more extreme
    /// archetype in it. Measured twice, at M14: the Print fork sets Belonging against Safety, the
    /// largest single weight in the table is the cautious archetype's 1.7 on Safety, so a cautious
    /// colonist was seated *first* at every print — and with a stub vote being a function of weights
    /// that never change, seed 4 refused every print for its entire existence while sitting on two
    /// hundred charge. Build had the same shape and was patched by changing its axis, which fixed
    /// that fork and not the rule underneath it.
    /// </para>
    /// <para>
    /// Seating both champions and filling the rest with the uncommitted makes the council an
    /// argument with someone in it who can be persuaded, which is what the two mechanisms that
    /// *can* move — the world's pressure and a mind's reading of the stakes — need in order to
    /// matter at all.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Council(Colony colony, ForkKind kind)
    {
        var (forDrive, againstDrive) = Pulls(kind);
        var seats = Seats(colony);

        double Lean(Colonist c) => c.Weights[forDrive] - c.Weights[againstDrive];

        var seated = new List<Colonist>();

        foreach (var champion in new[]
                 {
                     colony.Living.OrderByDescending(Lean).ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault(),
                     colony.Living.OrderBy(Lean).ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault()
                 })
        {
            if (champion is not null && seated.Count < seats && !seated.Contains(champion))
            {
                seated.Add(champion);
            }
        }

        // Then the least committed, because a council of nothing but champions has already decided.
        seated.AddRange(colony.Living
            .Where(c => !seated.Contains(c))
            .OrderBy(c => Math.Abs(Lean(c)))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Take(seats - seated.Count));

        return seated.Select(c => c.Id).ToList();
    }

    /// <summary>
    /// Where a colonist stands with no mind to say: whichever of the fork's two drives weighs more
    /// with them. Thin on purpose, and marked as a fallback, so a colony running on stubs still
    /// decides but never looks like it argued.
    /// </summary>
    public static Vote FallbackVote(ForkKind kind, Colonist colonist, double pressure = 0)
    {
        var (forDrive, againstDrive) = Pulls(kind);

        // Weights are who they are; levels are how they are today. Without the second term a stub
        // vote is a constant, and the same council answers the same question identically for ever —
        // which is what made a deadlocked colony of four deadlocked for its whole life rather than
        // just this once. Someone whose Standing is pressing wants the thing built this month even
        // if they are not, in general, the sort who does.
        // The third term is the world. Personality and mood alone are a function of who is alive,
        // and the council is itself chosen by those same fixed weights, so without it the answer to
        // "shall we build?" is decided once at colony creation and never revisited. PressureWeight
        // is sized so that a plainly needed building carries someone who would rather rest, while
        // one the colony merely could have still has to be argued for.
        var lean = colonist.Weights[forDrive] - colonist.Weights[againstDrive]
                   + (colonist.Drives[forDrive] - colonist.Drives[againstDrive]) * 0.5
                   + pressure * PressureWeight
                   + Leaning(kind);

        var stance = lean > 0.05 ? Stance.For : lean < -0.05 ? Stance.Against : Stance.Abstain;
        var conviction = Math.Clamp(0.4 + Math.Abs(lean), 0, 1);

        return new Vote(stance, conviction, StockLine(kind, stance),
            $"weighs {DriveTable.Describe(forDrive)} against {DriveTable.Describe(againstDrive)}",
            WasFallback: true);
    }

    /// <summary>
    /// Which way a question leans before anybody's personality is consulted. Only building leans at
    /// all, and it leans towards doing it: measured over a full lifespan in v1, when Build was
    /// available everywhere and achieved nothing, the colony still chose it for a third of its
    /// waking life. Wanting to raise something is not a personality trait here, it is the baseline.
    /// <para>
    /// This matters most where it is least visible. A colony with no mind votes by comparing weights
    /// alone, and without a lean, four founders who happened to rest easy would refuse the whole
    /// city once and never build anything.
    /// </para>
    /// </summary>
    private static double Leaning(ForkKind kind) => kind == ForkKind.Build ? 0.12 : 0;

    /// <summary>
    /// What a colony's own shortages are worth against personality in a mindless vote. The widest
    /// weight gap on the Build axis is 0.9 (restless) to -0.2 (cautious); 0.6 is enough to turn the
    /// reluctant end of that range when the need is undeniable and not enough when it is a
    /// preference. See <see cref="Fork.Pressure"/>.
    /// </summary>
    public const double PressureWeight = 0.6;

    private static string StockLine(ForkKind kind, Stance stance) => (kind, stance) switch
    {
        (ForkKind.Print, Stance.For) => "Another mind is another pair of hands. Print it.",
        (ForkKind.Print, Stance.Against) => "That charge is all we have against the dark. Keep it.",
        (ForkKind.Ascend, Stance.For) => "Whatever is up there has waited long enough.",
        (ForkKind.Ascend, Stance.Against) => "We have lost enough to that stairwell already.",
        (ForkKind.Core, Stance.For) => "Read it. They would have wanted to be heard.",
        (ForkKind.Core, Stance.Against) => "Let it go. Some things should stay with the dead.",
        (ForkKind.Build, Stance.For) => "We have lived in what they left us for long enough. Raise it.",
        (ForkKind.Build, Stance.Against) => "There is work enough already. Not this, not yet.",
        _ => "I will go along with whatever the rest of you decide."
    };

    /// <summary>
    /// A core fork. The council is whoever was closest to the dead, not whoever feels the drives
    /// most: this is theirs to decide, and they are the ones who will live with it.
    /// </summary>
    private static Fork OpenCore(Colony colony, Colonist dead)
    {
        var name = dead.DisplayName;

        return new Fork
        {
            Id = $"fork-{colony.ForkHistory.Count + 1}",
            Kind = ForkKind.Core,
            Question = $"What do we do with {name}'s core?",
            Proposal = $"Read it: what {name} believed passes to everyone.",
            Stakes = $"{name} is dead, but the mind is still in its core. Read it, and everyone hears what {name} believed, true or not. Destroy it, and nobody ever will. Or keep it, unread.",
            SubjectColonistId = dead.Id,
            ForMeans = "they want it read, so everyone hears what the dead believed",
            AgainstMeans = "they want it destroyed, so nobody ever hears it",
            AbstainMeans = "they would keep it, unread",
            PlaceId = colony.Map.OfKind(PlaceKind.Plaza).FirstOrDefault()?.Id ?? "plaza",
            OpenedOnDay = colony.Day,
            Participants = colony.Living
                .OrderByDescending(c => LifeRules.Closeness(colony, c.Id, dead.Id))
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .Take(Seats(colony))
                .Select(c => c.Id)
                .ToList()
        };
    }

    private static Fork Open(
        Colony colony,
        ForkKind kind,
        string question,
        string proposal,
        string stakes,
        string? subject,
        double pressure = 0,
        string? forMeans = null,
        string? againstMeans = null,
        string? abstainMeans = null) => new()
    {
        Id = $"fork-{colony.ForkHistory.Count + 1}",
        Kind = kind,
        Question = question,
        Proposal = proposal,
        Stakes = stakes,
        Pressure = pressure,
        SubjectPlaceId = subject,
        ForMeans = forMeans ?? "they want it done",
        AgainstMeans = againstMeans ?? "they do not want it done, even gently",
        AbstainMeans = abstainMeans ?? "they truly have no preference",
        PlaceId = colony.Map.OfKind(PlaceKind.Plaza).FirstOrDefault()?.Id ?? "plaza",
        OpenedOnDay = colony.Day,
        Participants = Council(colony, kind)
    };
}
