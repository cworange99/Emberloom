using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Events;
using Emberloom.Core.World;

namespace Emberloom.Core.Forks;

/// <summary>The questions a colony can put to itself. Each has a consequence the body applies when it is decided.</summary>
public enum ForkKind
{
    /// <summary>The Loom has charge for another mind. Spend it, or keep it?</summary>
    Print,

    /// <summary>A way up has been cleared. Go further, or leave what is up there alone?</summary>
    Ascend
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

    /// <summary>Conviction-weighted: a colonist who barely cares moves the colony less than one who does.</summary>
    public (double For, double Against) Tally() => (
        _votes.Values.Where(v => v.Stance == Stance.For).Sum(v => v.Conviction),
        _votes.Values.Where(v => v.Stance == Stance.Against).Sum(v => v.Conviction));

    internal void Decide(bool carried, double day)
    {
        Carried = carried;
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

    /// <summary>A question the colony said no to comes back, but not the next morning.</summary>
    public const double PrintAskAgainDays = 6.0;

    public const double AscendAskAgainDays = 8.0;

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

        return null;
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
        _ => (Drive.Meaning, Drive.Safety)
    };

    /// <summary>
    /// Whoever feels it most, either way: the colonists whose weights on the two drives sit furthest
    /// from ordinary. A council of the indifferent does not argue, and an argument is the point.
    /// </summary>
    public static IReadOnlyList<string> Council(Colony colony, ForkKind kind)
    {
        var (forDrive, againstDrive) = Pulls(kind);

        return colony.Living
            .OrderByDescending(c => Math.Abs(c.Weights[forDrive] - 1.0) + Math.Abs(c.Weights[againstDrive] - 1.0))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Take(CouncilSize)
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
        var lean = colonist.Weights[forDrive] - colonist.Weights[againstDrive];

        var stance = lean > 0.05 ? Stance.For : lean < -0.05 ? Stance.Against : Stance.Abstain;
        var conviction = Math.Clamp(0.4 + Math.Abs(lean), 0, 1);

        return new Vote(stance, conviction, StockLine(kind, stance),
            $"weighs {DriveTable.Describe(forDrive)} against {DriveTable.Describe(againstDrive)}",
            WasFallback: true);
    }

    private static string StockLine(ForkKind kind, Stance stance) => (kind, stance) switch
    {
        (ForkKind.Print, Stance.For) => "Another mind is another pair of hands. Print it.",
        (ForkKind.Print, Stance.Against) => "That charge is all we have against the dark. Keep it.",
        (ForkKind.Ascend, Stance.For) => "Whatever is up there has waited long enough.",
        (ForkKind.Ascend, Stance.Against) => "We have lost enough to that stairwell already.",
        _ => "I will go along with whatever the rest of you decide."
    };

    private static Fork Open(
        Colony colony,
        ForkKind kind,
        string question,
        string proposal,
        string stakes,
        string? subject) => new()
    {
        Id = $"fork-{colony.ForkHistory.Count + 1}",
        Kind = kind,
        Question = question,
        Proposal = proposal,
        Stakes = stakes,
        SubjectPlaceId = subject,
        PlaceId = colony.Map.OfKind(PlaceKind.Plaza).FirstOrDefault()?.Id ?? "plaza",
        OpenedOnDay = colony.Day,
        Participants = Council(colony, kind)
    };
}
