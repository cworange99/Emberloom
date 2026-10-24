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
    /// A print is not worth arguing about until the Loom holds more than one costs: the colony wants
    /// something left over for light and doors afterwards.
    /// </summary>
    public const double PrintThreshold = Colony.PrintCost + 20;

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

        if (colony.LoomCharge >= PrintThreshold && colony.Day >= colony.AskAgainOn(ForkKind.Print))
        {
            return Open(colony, ForkKind.Print,
                question: "Do we let the Loom print another mind?",
                proposal: "The Loom spends its charge on one more mind.",
                // Both sides spelled out as sides. Live at M5, a looser version ("kept, it could light
                // the dark places") was read by a 3B model as a reason to print.
                stakes: "For: a print adds one more mind to the colony — more company, more hands. Against: the charge is spent and gone; kept, it could light the dark places or open a door that will not open.",
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

    public static IReadOnlyList<string> Council(Colony colony, ForkKind kind)
    {
        var (forDrive, againstDrive) = Pulls(kind);

        return colony.Living
            .OrderByDescending(c => Math.Abs(c.Weights[forDrive] - 1.0) + Math.Abs(c.Weights[againstDrive] - 1.0))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Take(Seats(colony))
            .Select(c => c.Id)
            .ToList();
    }

    /// <summary>
    /// Where a colonist stands with no mind to say: whichever of the fork's two drives weighs more
    /// with them. Thin on purpose, and marked as a fallback, so a colony running on stubs still
    /// decides but never looks like it argued.
    /// </summary>
    public static Vote FallbackVote(ForkKind kind, Colonist colonist)
    {
        var (forDrive, againstDrive) = Pulls(kind);

        // Weights are who they are; levels are how they are today. Without the second term a stub
        // vote is a constant, and the same council answers the same question identically for ever —
        // which is what made a deadlocked colony of four deadlocked for its whole life rather than
        // just this once. Someone whose Standing is pressing wants the thing built this month even
        // if they are not, in general, the sort who does.
        var lean = colonist.Weights[forDrive] - colonist.Weights[againstDrive]
                   + (colonist.Drives[forDrive] - colonist.Drives[againstDrive]) * 0.5
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
        string? forMeans = null,
        string? againstMeans = null,
        string? abstainMeans = null) => new()
    {
        Id = $"fork-{colony.ForkHistory.Count + 1}",
        Kind = kind,
        Question = question,
        Proposal = proposal,
        Stakes = stakes,
        SubjectPlaceId = subject,
        ForMeans = forMeans ?? "they want it done",
        AgainstMeans = againstMeans ?? "they do not want it done, even gently",
        AbstainMeans = abstainMeans ?? "they truly have no preference",
        PlaceId = colony.Map.OfKind(PlaceKind.Plaza).FirstOrDefault()?.Id ?? "plaza",
        OpenedOnDay = colony.Day,
        Participants = Council(colony, kind)
    };
}
