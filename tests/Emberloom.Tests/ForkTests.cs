using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Forks;
using Emberloom.Core.Simulation;
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

    private static Fork OpenPrint(Colony colony)
    {
        colony.LoomCharge = ForkRules.PrintThreshold + 1;
        var fork = ForkRules.Detect(colony);
        Assert.NotNull(fork);
        colony.OpenFork = fork;
        return fork!;
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
        colony.LoomCharge = ForkRules.PrintThreshold + 1;

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
        colony.LoomCharge = ForkRules.PrintThreshold + 10;

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
        colony.LoomCharge = ForkRules.PrintThreshold + 10;
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
        Assert.Null(ForkRules.Detect(colony));
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
    /// The timeout is in colony days, and at 60x a colony day passes in seconds. A fork still waiting
    /// on its council's minds must not decide without them just because the clock is running fast.
    /// </summary>
    [Fact]
    public void AForkStillWaitingOnMindsDoesNotTimeOut()
    {
        var (colony, body) = Start();
        var fork = OpenPrint(colony);
        fork.AwaitingMinds = 3;

        for (var tick = 0; tick < (ForkRules.TimeoutDays + 0.5) * Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        Assert.True(fork.IsOpen);
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
