using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Forks;
using Emberloom.Core.Simulation;
using Emberloom.Core.World;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M5 forks, on the body alone: when a colony faces a genuine question, who sits on the council,
/// what the outcome does, and that a colony with no mind at all still gets its turning points.
/// </summary>
public class ForkTests
{
    private static (Colony Colony, BodySimulation Body) Start(int seed = 11)
    {
        var random = new Random(seed);
        var colony = ColonyFactory.Create(8, random);
        colony.Tick = (long)(ForkRules.EarliestDay * Colony.TicksPerDay);
        return (colony, new BodySimulation(colony, random));
    }

    /// <summary>
    /// From M14 a print needs both the charge and somewhere to have kept it: the Loom's own cells
    /// hold sixty, and what a print plus two months of one more mouth costs is more than that at
    /// any population. A colony that has not raised a granary cannot print, which is a real rule
    /// rather than a test convenience — see Colony.StoreCapacity.
    /// </summary>
    private static Fork OpenPrint(Colony colony)
    {
        Store(colony);
        colony.LoomCharge = ForkRules.ChargeNeededToPrint(colony) + 1;
        var fork = ForkRules.Detect(colony);
        Assert.NotNull(fork);
        colony.OpenFork = fork;
        return fork!;
    }

    private static void Store(Colony colony)
    {
        foreach (var site in colony.Map.Places.Where(p => p.Becomes == PlaceKind.Granary).Take(2))
        {
            site.Kind = PlaceKind.Granary;
            site.BuildDone = site.BuildRequired;
        }
    }

    private static void EveryoneVotes(Fork fork, Stance stance)
    {
        foreach (var id in fork.Participants)
        {
            fork.Cast(id, new Vote(stance, 1.0, "said so", "because", WasFallback: false));
        }
    }

    [Fact]
    public void APrintForkOpensWhenTheLoomHasChargeToSpare()
    {
        var (colony, body) = Start();
        Store(colony);
        colony.LoomCharge = ForkRules.ChargeNeededToPrint(colony) + 1;

        Fork? opened = null;
        body.ForkOpened += f => opened = f;

        for (var i = 0; i < 20 && opened is null; i++)
        {
            body.Tick();
        }

        Assert.NotNull(opened);
        Assert.Equal(ForkKind.Print, opened!.Kind);
        Assert.Same(opened, colony.OpenFork);
        Assert.Equal(ForkRules.CouncilSize, opened.Participants.Count);
        Assert.Equal(opened.Participants.Count, opened.Participants.Distinct().Count());
    }

    [Fact]
    public void NoForkOpensBeforeTheColonyHasHadTimeToBecomeAnyone()
    {
        var colony = ColonyFactory.Create(8, new Random(11));
        colony.LoomCharge = ForkRules.ChargeNeededToPrint(colony) + 10;

        Assert.Null(ForkRules.Detect(colony));
    }

    /// <summary>
    /// Live at M5, the first fork opened while the laptop was still writing the colony's first
    /// minds, and was argued by colonists with no personalities yet.
    /// </summary>
    [Fact]
    public void NoForkOpensWhileAnyoneIsStillBeingKindled()
    {
        var (colony, _) = Start();
        colony.LoomCharge = ForkRules.ChargeNeededToPrint(colony) + 10;
        colony.Colonists[3].AwaitingKindling = true;

        Assert.Null(ForkRules.Detect(colony));

        colony.Colonists[3].AwaitingKindling = false;
        Assert.NotNull(ForkRules.Detect(colony));
    }

    [Fact]
    public void TheCouncilIsWhoeverFeelsItMost()
    {
        var (colony, _) = Start();
        var passionate = colony.Colonists[^1];

        for (var i = 0; i < 20; i++)
        {
            passionate.Weights.Drift(Drive.Belonging, 0.2);
            passionate.Weights.Drift(Drive.Safety, -0.2);
        }

        Assert.Contains(passionate.Id, OpenPrint(colony).Participants);
    }

    [Fact]
    public void APrintTheColonyAgreesToAddsAMindAndSpendsTheCharge()
    {
        var (colony, body) = Start();
        var fork = OpenPrint(colony);
        EveryoneVotes(fork, Stance.For);

        var before = colony.LoomCharge;
        Colonist? printed = null;
        body.ForkResolved += (_, p) => printed = p;

        body.Tick();

        Assert.True(fork.Carried);
        Assert.Null(colony.OpenFork);
        Assert.Contains(fork, colony.ForkHistory);
        Assert.NotNull(printed);
        Assert.Equal(9, colony.Colonists.Count);
        Assert.True(colony.LoomCharge < before - Colony.PrintCost + 0.1);

        // Everyone who argued it remembers that the colony went their way.
        var member = colony.Find(fork.Participants[0])!;
        Assert.Contains(member.Memory.Episodic, m => m.Verb.StartsWith("argued for it, and the colony agreed", StringComparison.Ordinal));
    }

    [Fact]
    public void ANoPutsTheQuestionOffRatherThanAskingAgainAtOnce()
    {
        var (colony, body) = Start();
        var fork = OpenPrint(colony);
        EveryoneVotes(fork, Stance.Against);

        body.Tick();

        Assert.False(fork.Carried);
        Assert.Equal(8, colony.Colonists.Count);
        Assert.True(colony.AskAgainOn(ForkKind.Print) >= colony.Day + ForkRules.PrintAskAgainDays - 0.01);

        // The *print* is what was put off. Since M10 the colony always has ground it could raise,
        // so there is nearly always some fork available — which is the point of that milestone.
        Assert.NotEqual(ForkKind.Print, ForkRules.Detect(colony)?.Kind);
    }

    [Fact]
    public void ATieLeavesThingsAsTheyAre()
    {
        var (colony, body) = Start();
        var fork = OpenPrint(colony);

        fork.Cast(fork.Participants[0], new Vote(Stance.For, 0.6, "yes", "", false));
        fork.Cast(fork.Participants[1], new Vote(Stance.Against, 0.6, "no", "", false));
        foreach (var id in fork.Participants.Skip(2))
        {
            fork.Cast(id, new Vote(Stance.Abstain, 0.5, "either way", "", false));
        }

        body.Tick();

        Assert.False(fork.Carried);
        Assert.Equal(8, colony.Colonists.Count);
    }

    /// <summary>
    /// A colony with no mind never hears a stance from anyone — and still has to be able to decide,
    /// or its turning points never arrive. It decides on where each member's weights stand, and
    /// every such vote is marked as a fallback.
    /// </summary>
    [Fact]
    public void WithNoMindTheForkIsStillDecidedOnWhereEveryoneStands()
    {
        var (colony, body) = Start();
        var fork = OpenPrint(colony);

        for (var tick = 0; tick < (ForkRules.TimeoutDays + 0.1) * Colony.TicksPerDay && fork.IsOpen; tick++)
        {
            body.Tick();
        }

        Assert.False(fork.IsOpen);
        Assert.All(fork.Votes.Values, v => Assert.True(v.WasFallback));

        Assert.Equal(fork.HasMajority, fork.Carried);
    }

    /// <summary>
    /// Live at M5, one "for" and four abstentions printed a mind. A fork takes a majority of the
    /// whole council, by heads; however sure the lone voice is, it does not carry.
    /// </summary>
    [Fact]
    public void OneVoiceDoesNotCarryAFork()
    {
        var (colony, body) = Start();
        var fork = OpenPrint(colony);

        fork.Cast(fork.Participants[0], new Vote(Stance.For, 1.0, "print it", "", false));
        foreach (var id in fork.Participants.Skip(1))
        {
            fork.Cast(id, new Vote(Stance.Abstain, 0.1, "either way", "", false));
        }

        body.Tick();

        Assert.False(fork.Carried);
        Assert.Equal(8, colony.Colonists.Count);
    }

    /// <summary>Three of five carries it, however strongly the other two object.</summary>
    [Fact]
    public void AMajorityCarriesItOverFirmObjection()
    {
        var (colony, body) = Start();
        var fork = OpenPrint(colony);

        foreach (var (id, i) in fork.Participants.Select((id, i) => (id, i)))
        {
            fork.Cast(id, i < 3
                ? new Vote(Stance.For, 0.2, "fine", "", false)
                : new Vote(Stance.Against, 1.0, "never", "", false));
        }

        body.Tick();

        Assert.Equal(3, fork.Needed);
        Assert.True(fork.Carried);
    }

    /// <summary>
    /// **The council must not be able to argue a question away while it is being asked.**
    /// <para>
    /// A fork is decided in the plaza, and <c>Deliberate</c> relieves Belonging at 1.20 a day — the
    /// strongest supply of it anywhere in the catalogue. The Print fork weighs Belonging against
    /// Safety. So debating whether the colony is lonely enough to want another person cured the
    /// loneliness, and nobody wanted one by the time the vote was counted: seed 4 was asked sixteen
    /// times over 2,160 days and refused every one, twice from a position where every seated voter
    /// had been in favour when the question was put.
    /// </para>
    /// <para>
    /// Nothing the council does to itself during the vote may move the vote. The personal half of a
    /// mindless vote is read once, when the question is put, and carried on the fork beside the
    /// pressure that is already snapshotted for exactly the same reason.
    /// </para>
    /// </summary>
    [Fact]
    public void DeliberatingDoesNotTalkTheCouncilOutOfTheQuestion()
    {
        var (colony, body) = Start();
        var fork = OpenPrint(colony);

        var before = fork.Participants
            .Select(id => colony.Find(id)!)
            .ToDictionary(c => c.Id, c => ForkRules.Standing(ForkKind.Print, c));

        // Exactly what sitting in council does to them, and nothing else: the drive the question
        // turns on, satisfied by the act of discussing it.
        foreach (var id in fork.Participants)
        {
            colony.Find(id)!.Drives.Relieve(ActionCatalog.Deliberate.Relief, 3.0);
        }

        Assert.All(fork.Participants, id =>
            Assert.True(ForkRules.Standing(ForkKind.Print, colony.Find(id)!) < before[id] - 0.05,
                "the test's own premise: deliberating should have moved them against"));

        foreach (var id in fork.Participants)
        {
            Assert.Equal(before[id], fork.Standing[id], 6);
        }

        for (var tick = 0; tick < (ForkRules.TimeoutDays + 0.5) * Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        Assert.False(fork.IsOpen);

        // Every vote cast on what they came in with, not on what the council did to them.
        foreach (var (id, vote) in fork.Spoken)
        {
            var expected = ForkRules.FallbackVote(ForkKind.Print, colony.Find(id)!, fork.Pressure, before[id]);
            Assert.Equal(expected.Stance, vote.Stance);
        }
    }

        /// <summary>
    /// A seat the governor never found room for is a vote that will never arrive, and a fork with
    /// nothing left to wait for must be counted rather than stand until its deadline. It stood:
    /// three colony-days at 1x is three real hours, and the auto-slow holds the clock there — which
    /// is how one vote took over an hour of real time in front of a question nobody was answering.
    /// </summary>
    [Fact]
    public void AForkWithNothingLeftToWaitForIsCountedAtOnce()
    {
        var (colony, body) = Start();
        var fork = OpenPrint(colony);

        // Two seats were handed to a mind and the third was refused by a full queue.
        fork.MindsAsked = 2;
        fork.AwaitingMinds = 2;

        body.Tick();
        Assert.True(fork.IsOpen);

        // Both of those minds come back without a vote between them, which is what a pair of
        // failed calls leaves behind. Nothing further can arrive.
        fork.AwaitingMinds = 0;
        body.Tick();

        Assert.False(fork.IsOpen);
        Assert.True(colony.Day - (fork.ResolvedOnDay ?? 0) <= 0);
        Assert.True(fork.ResolvedOnDay - fork.OpenedOnDay < ForkRules.TimeoutDays);
    }

    /// <summary>
    /// A vote is bounded in colony-days whether or not minds are still working on it. It used to
    /// wait for them however long they took, which is right at a speed somebody is watching and
    /// wrong at one nobody is: nothing bounded it in colony time, so at Overnight one fork stood
    /// the council in the plaza for a dozen colony-days — and deliberating relieves the very drives
    /// the fork is weighed on. Anyone whose mind has not come back is counted by where they stood.
    /// </summary>
    [Fact]
    public void AForkIsCountedOnceItsDeadlinePassesEvenWithMindsStillWorking()
    {
        var (colony, body) = Start();
        var fork = OpenPrint(colony);
        fork.AwaitingMinds = 3;

        for (var tick = 0; tick < (ForkRules.TimeoutDays - 0.5) * Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        Assert.True(fork.IsOpen);

        for (var tick = 0; tick < Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        Assert.False(fork.IsOpen);
        Assert.All(fork.Votes.Values, v => Assert.True(v.WasFallback));
    }

    [Fact]
    public void AFloorAboveAClearedStairwellWaitsForTheColony()
    {
        var (colony, body) = Start();
        var stair = colony.Map["east-stair"];
        var ninth = colony.Map["ninth-floor"];

        stair.ClearingDone = stair.ClearingRequired;
        ForkRules.HoldTheWayUp(colony, stair);
        Assert.Contains(ninth.Id, colony.ForbiddenPlaces);

        // Nobody's scoring reaches it, even from the top of the stairs.
        var climber = colony.Colonists[0];
        climber.PlaceId = stair.Id;
        Assert.DoesNotContain(body.Scorer.Score(climber, colony), o => o.Target.Id == ninth.Id);

        // The colony is asked, and when it agrees, the way is open.
        var fork = ForkRules.Detect(colony);
        Assert.NotNull(fork);
        Assert.Equal(ForkKind.Ascend, fork!.Kind);
        Assert.Equal(ninth.Id, fork.SubjectPlaceId);

        colony.OpenFork = fork;
        EveryoneVotes(fork, Stance.For);
        body.Tick();

        Assert.DoesNotContain(ninth.Id, colony.ForbiddenPlaces);
        Assert.Contains(body.Scorer.Score(climber, colony), o => o.Target.Id == ninth.Id);
    }

    [Fact]
    public void TheCouncilIsDrawnToThePlazaAndNobodyElseIs()
    {
        var (colony, body) = Start();
        var fork = OpenPrint(colony);

        var member = colony.Find(fork.Participants[0])!;
        var outsider = colony.Living.First(c => !fork.Participants.Contains(c.Id));

        Assert.Contains(body.Scorer.Score(member, colony),
            o => o.Definition.Kind == ActionKind.Deliberate && o.Target.Id == fork.PlaceId);
        Assert.DoesNotContain(body.Scorer.Score(outsider, colony), o => o.Definition.Kind == ActionKind.Deliberate);
    }

    [Fact]
    public void AVoteAfterTheForkIsDecidedIsRefused()
    {
        var (colony, body) = Start();
        var fork = OpenPrint(colony);
        EveryoneVotes(fork, Stance.For);
        body.Tick();

        Assert.False(fork.Cast(fork.Participants[0], new Vote(Stance.Against, 1, "too late", "", false)));
    }
}
