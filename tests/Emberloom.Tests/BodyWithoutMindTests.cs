using Emberloom.Core.Actions;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Simulation;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// The correctness test the whole architecture leans on: with no mind at all — no Ollama, no
/// governor, no model calls anywhere — the colony still forages, salvages, builds, argues and
/// remembers. Nothing in this file may reference Emberloom.Mind.
/// </summary>
public class BodyWithoutMindTests
{
    private static (Colony Colony, BodySimulation Body) Run(int ticks, int colonists = 8, int seed = 4)
    {
        var random = new Random(seed);
        var colony = ColonyFactory.Create(colonists, random);
        var body = new BodySimulation(colony, random);

        for (var i = 0; i < ticks; i++)
        {
            body.Tick();
        }

        return (colony, body);
    }

    [Fact]
    public void SimulationRunsForManyDaysWithoutAModel()
    {
        var (colony, _) = Run(Colony.TicksPerDay * 30);

        Assert.Equal(30, colony.Day, precision: 6);
        Assert.All(colony.Living, c => Assert.True(c.IsAlive));
        Assert.True(colony.Feed.Events.Count > 0, "the feed should have something to read");
    }

    [Fact]
    public void ColonistsActRatherThanStandingStill()
    {
        var (colony, _) = Run(Colony.TicksPerDay * 10);

        // Every colonist should have accumulated real episodic history from doing things.
        Assert.All(colony.Living, colonist =>
            Assert.True(colonist.Memory.Episodic.Count > 5,
                $"{colonist.DisplayName} recorded only {colonist.Memory.Episodic.Count} memories"));
    }

    [Fact]
    public void DrivesAreActuallyRelievedRatherThanClimbingForever()
    {
        var (colony, _) = Run(Colony.TicksPerDay * 20);

        // Sustenance is the easiest drive to satisfy — the forest is generous. If the loop works
        // at all, nobody should be sitting at maximum need on it.
        var starving = colony.Living.Where(c => c.Drives[Drive.Sustenance] > 0.95).ToList();
        Assert.True(starving.Count == 0,
            $"{starving.Count} colonists never ate: {string.Join(", ", starving.Select(c => c.DisplayName))}");
    }

    [Fact]
    public void ColonistsSpreadOutAcrossTheCityRatherThanPilingUp()
    {
        var (colony, _) = Run(Colony.TicksPerDay * 12);

        var occupied = colony.Living.Select(c => c.PlaceId).Distinct().Count();
        Assert.True(occupied > 1, "the whole colony ended up standing in one place");
    }

    [Fact]
    public void TheColonyExploresAndSalvages()
    {
        var (colony, _) = Run(Colony.TicksPerDay * 40, colonists: 10);

        Assert.True(colony.ArtifactsRecovered > 0, "nobody recovered anything of the makers'");
    }

    [Fact]
    public void RelationshipsFormBetweenColonistsWhoShareTime()
    {
        var (colony, _) = Run(Colony.TicksPerDay * 25, colonists: 8);

        var known = colony.Living
            .SelectMany(c => colony.Relationships.Outgoing(c.Id))
            .Any(r => r.SharedMemories > 0);

        Assert.True(known, "no colonist ever registered time spent with another");
    }

    [Fact]
    public void EpisodicMemoryIsPrunedRatherThanGrowingWithoutBound()
    {
        var (colony, _) = Run(Colony.TicksPerDay * 90, colonists: 6);

        Assert.All(colony.Living, colonist =>
            Assert.True(colonist.Memory.Episodic.Count <= 500,
                $"{colonist.DisplayName} is holding {colonist.Memory.Episodic.Count} episodic rows"));
    }

    [Fact]
    public void MemoryPressureEventuallyWantsAReflection()
    {
        var (colony, _) = Run(Colony.TicksPerDay * 15, colonists: 6);

        // In M1 nothing serves these, but the trigger the governor will read must already fire.
        Assert.Contains(colony.Living, c => c.Memory.WantsReflection(colony.Day));
    }

    /// <summary>
    /// The seam. Personality weights are the only thing Layer 2 contributes to action selection,
    /// so swapping them must visibly change behaviour with no other code change. If this test
    /// ever goes flat, Kindling is decorative and the whole design is in trouble.
    /// </summary>
    [Fact]
    public void PersonalityWeightsVisiblyChangeBehaviour()
    {
        // Measured while the choice is still live. Dangerous work is a finite resource — each
        // artifact is findable exactly once — so over a long run the bold colonies strip the city
        // early and then have nothing risky left to do, which flatters the cautious ones on any
        // whole-run tally. What the seam has to show is disposition, not eventual totals.
        var curious = RiskTakenUnder(Archetypes.Curious.Weights);
        var cautious = RiskTakenUnder(Archetypes.Cautious.Weights);

        Assert.True(curious.Artifacts > cautious.Artifacts,
            $"curious colonists recovered {curious.Artifacts} artifacts, cautious ones {cautious.Artifacts} — " +
            "the personality seam is not affecting action selection");

        Assert.True(curious.DangerousTicks > cautious.DangerousTicks,
            $"curious colonists spent {curious.DangerousTicks} ticks on dangerous work, " +
            $"cautious ones {cautious.DangerousTicks}");
    }

    /// <summary>
    /// A colony where everyone shares one archetype, run only until the risky work starts running
    /// out. Returns what they were willing to go after in that window.
    /// </summary>
    private static (int Artifacts, int DangerousTicks) RiskTakenUnder(PersonalityWeights weights)
    {
        var random = new Random(11);
        var colony = ColonyFactory.Create(6, random);

        foreach (var colonist in colony.Colonists)
        {
            colonist.Weights = weights.Clone();
        }

        var body = new BodySimulation(colony, random);
        var dangerousTicks = 0;

        for (var i = 0; i < Colony.TicksPerDay * 8; i++)
        {
            body.Tick();
            dangerousTicks += colony.Living.Count(c =>
                c.Current?.Definition.Kind is ActionKind.Salvage or ActionKind.Clear);
        }

        return (colony.ArtifactsRecovered, dangerousTicks);
    }
}
