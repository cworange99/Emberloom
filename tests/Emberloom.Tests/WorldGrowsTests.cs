using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Forks;
using Emberloom.Core.Simulation;
using Emberloom.Core.World;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M10 — the world grows. Version 1 only ever cleared what the makers left; the colony could
/// <c>Build</c> anywhere and it changed nothing at all, which measured out at 34% of every
/// colonist's waking life spent on work that left no mark. The colony now votes on ground, raises
/// one building at a time, and the city it ends with is not the city it started in.
/// </summary>
public class WorldGrowsTests
{
    private static (Colony Colony, BodySimulation Body) Start(int seed = 11, int colonists = 4)
    {
        var random = new Random(seed);
        var colony = ColonyFactory.Create(colonists, random, seed);
        colony.Tick = (long)(ForkRules.EarliestDay * Colony.TicksPerDay);
        return (colony, new BodySimulation(colony, random));
    }

    private static Fork OpenBuild(Colony colony)
    {
        var fork = ForkRules.Detect(colony);
        Assert.NotNull(fork);
        Assert.Equal(ForkKind.Build, fork!.Kind);
        colony.OpenFork = fork;
        return fork;
    }

    private static void EveryoneVotes(Fork fork, Stance stance)
    {
        foreach (var id in fork.Participants)
        {
            fork.Cast(id, new Vote(stance, 1.0, "said so", "because", WasFallback: false));
        }
    }

    [Fact]
    public void EverySiteIsAGraphLeaf()
    {
        var map = CityMap.CreateDefault();
        var sites = map.Places.Where(p => p.Kind == PlaceKind.Site).ToList();

        Assert.NotEmpty(sites);

        // Reachable() walks neighbours, so a site standing between two built places would put
        // itself on the only route between them. Leaves cannot, which is why they are leaves.
        Assert.All(sites, site => Assert.Single(site.Neighbours));
    }

    [Fact]
    public void BuildIsOnlyOfferedOnGroundTheColonyAgreedTo()
    {
        var (colony, _) = Start();
        var site = colony.Map.Places.First(p => p.Kind == PlaceKind.Site);
        var scorer = new UtilityScorer(new Random(1));
        var colonist = colony.Living.First();

        // Unapproved: nobody is offered the work, however much they want Meaning.
        colonist.PlaceId = site.Id;
        Assert.DoesNotContain(
            scorer.Score(colonist, colony),
            option => option.Definition.Kind == ActionKind.Build);

        site.IsApproved = true;
        Assert.Contains(
            scorer.Score(colonist, colony),
            option => option.Definition.Kind == ActionKind.Build && option.Target.Id == site.Id);
    }

    [Fact]
    public void ASiteBecomesTheBuildingItWasRaisedAs()
    {
        var (colony, body) = Start();
        var fork = OpenBuild(colony);
        var site = colony.Map.Find(fork.SubjectPlaceId!)!;
        var becomes = site.Becomes!.Value;

        EveryoneVotes(fork, Stance.For);
        body.Tick();

        Assert.True(site.IsApproved);
        Assert.Equal(PlaceKind.Site, site.Kind);

        // The labour, not the vote, is what raises it. The vote only makes the work possible.
        site.BuildDone = site.BuildRequired - 1;
        for (var tick = 0; tick < 400 && site.Kind == PlaceKind.Site; tick++)
        {
            body.Tick();
        }

        Assert.Equal(becomes, site.Kind);
        Assert.False(site.IsUnderConstruction);
        Assert.Contains(colony.Feed.Events, e => e.Text.Contains($"{site.Name} stands"));
    }

    [Fact]
    public void BuildingSomethingLiftsWhatEveryoneWasCarrying()
    {
        var (colony, body) = Start();
        var site = colony.Map.Places.First(p => p.Kind == PlaceKind.Site);
        site.IsApproved = true;
        site.BuildDone = site.BuildRequired - 1;

        var watcher = colony.Living.Last();
        watcher.Drives.Aggravate(Emberloom.Core.Drives.Drive.Meaning, 0.9);
        var before = watcher.Drives[Emberloom.Core.Drives.Drive.Meaning];

        var builder = colony.Living.First();
        builder.PlaceId = site.Id;
        builder.Current = null;

        for (var tick = 0; tick < 2000 && site.Kind == PlaceKind.Site; tick++)
        {
            body.Tick();
        }

        Assert.NotEqual(PlaceKind.Site, site.Kind);
        Assert.True(watcher.Drives[Emberloom.Core.Drives.Drive.Meaning] < before,
            "a colony that has raised something is a different proposition from one squatting in what it found");
    }

    [Fact]
    public void ANoSetsThatGroundAsideAndTheNextAskingIsAboutDifferentGround()
    {
        var (colony, body) = Start();
        var fork = OpenBuild(colony);
        var refused = fork.SubjectPlaceId!;

        EveryoneVotes(fork, Stance.Against);
        body.Tick();

        Assert.False(fork.Carried);
        Assert.True(colony.IsDeclined(refused));
        Assert.False(colony.Map.Find(refused)!.IsApproved);

        // The colony chooses *what* to build through a sequence of yes-or-no questions, so a no
        // has to move the question on rather than repeat it.
        colony.Tick += (long)(ForkRules.BuildAskAgainDays * Colony.TicksPerDay);
        var next = ForkRules.Detect(colony);

        Assert.Equal(ForkKind.Build, next?.Kind);
        Assert.NotEqual(refused, next!.SubjectPlaceId);
    }

    [Fact]
    public void OnlyOneSiteIsRaisedAtATime()
    {
        var (colony, body) = Start();
        var fork = OpenBuild(colony);
        EveryoneVotes(fork, Stance.For);
        body.Tick();

        // While one is going up the colony is not asked about another: a city built by a colony of
        // four, all at once, would be a city of foundations.
        colony.Tick += (long)(ForkRules.BuildAskAgainDays * Colony.TicksPerDay);
        Assert.NotEqual(ForkKind.Build, ForkRules.Detect(colony)?.Kind);
    }

    [Fact]
    public void AColonyWithNoMindStillBuildsItsCity()
    {
        var (colony, body) = Start(seed: 5);

        // No mind, so no deliberation is ever submitted and every fork times out onto the body's
        // own fallback votes. The city must still go up: everything in this milestone is Layer 1.
        for (var tick = 0; tick < 600 * Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        var raised = colony.Map.Places.Count(p => p.Kind is PlaceKind.Hall or PlaceKind.Granary or PlaceKind.Dwelling);
        Assert.True(raised > 0, "a colony running on stubs still has to be able to raise a building");
        Assert.Contains(colony.Feed.Events, e => e.Text.Contains("stands"));
    }

    [Fact]
    public void TheBodyProposesWhatIsMostObviouslyShort()
    {
        var (colony, _) = Start();

        // Beds first while people sleep in what the makers left: four colonists against two found
        // shelters is not short, so the first want is somewhere to be together.
        var first = ForkRules.Propose(colony);
        Assert.Equal(PlaceKind.Hall, first!.Becomes);

        first.Kind = PlaceKind.Hall;
        first.BuildDone = first.BuildRequired;

        var random = new Random(2);
        for (var i = 0; i < 12; i++)
        {
            ColonyFactory.Print(colony, random, chargeIsFree: true);
        }

        // Sixteen people and two shelters: now it is beds.
        Assert.Equal(PlaceKind.Dwelling, ForkRules.Propose(colony)!.Becomes);
    }

    /// <summary>
    /// Found at M12 by re-measuring M8's baseline: seed 4 — the seed the game ships with — refused
    /// all twelve sites, twenty votes over 600 days, and would have gone on refusing them for ever.
    /// A mindless vote read nothing but personality, and the council is itself selected by those
    /// same fixed weights, so the answer to "shall we build?" was settled when the colony was
    /// created. A colonist who would rather rest can still want a bed.
    /// </summary>
    [Fact]
    public void ACouncilThatWouldRatherRestStillVotesForABedWhenThereAreNone()
    {
        var (colony, _) = Start();
        var cautious = ColonyFactory.Print(colony, new Random(1), chargeIsFree: true)!;
        cautious.Weights.Vector[Drive.Rest] = 1.2;
        cautious.Weights.Vector[Drive.Meaning] = 1.0;

        var site = colony.Map.Places.First(p => p.Becomes == PlaceKind.Dwelling);

        // Nobody is short of anywhere to sleep, so there is nothing to weigh against temperament.
        Assert.Equal(0, ForkRules.Pressure(colony, site));
        Assert.NotEqual(Stance.For, ForkRules.FallbackVote(ForkKind.Build, cautious, 0).Stance);

        // Now fill the two found shelters well past three to a room. Nothing about the voter has
        // changed; what they are voting on has.
        var random = new Random(2);
        for (var i = 0; i < 12; i++)
        {
            ColonyFactory.Print(colony, random, chargeIsFree: true);
        }

        var pressure = ForkRules.Pressure(colony, site);
        Assert.True(pressure > 0.5, $"crowding should press hard, not gently (was {pressure:F2})");
        Assert.Equal(Stance.For, ForkRules.FallbackVote(ForkKind.Build, cautious, pressure).Stance);
    }

    /// <summary>
    /// The other half of the same fix: a building the colony merely could have still has to win an
    /// argument. Pressure that carried every question would be a rubber stamp with extra steps.
    /// </summary>
    [Fact]
    public void AStoreNobodyNeedsIsStillSomethingToArgueAbout()
    {
        var (colony, _) = Start();
        var cautious = ColonyFactory.Print(colony, new Random(1), chargeIsFree: true)!;
        cautious.Weights.Vector[Drive.Rest] = 1.2;
        cautious.Weights.Vector[Drive.Meaning] = 1.0;

        var stores = colony.Map.Places.Where(p => p.Becomes == PlaceKind.Granary).ToList();

        // Nowhere to put what is grown presses a little: enough that somebody who cares about the
        // colony's standing argues for it, not enough to carry someone who would rather rest.
        var firstStore = ForkRules.Pressure(colony, stores[0]);
        Assert.InRange(firstStore, 0.01, 0.2);
        Assert.NotEqual(Stance.For, ForkRules.FallbackVote(ForkKind.Build, cautious, firstStore).Stance);

        // And once one stands, the next is pure foresight and presses not at all.
        stores[0].Kind = PlaceKind.Granary;
        Assert.Equal(0, ForkRules.Pressure(colony, stores[1]));
    }
}
