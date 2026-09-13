using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Forks;
using Emberloom.Core.Life;
using Emberloom.Core.Memory;
using Emberloom.Core.Simulation;
using Emberloom.Mind;
using Emberloom.Mind.Ollama;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M7 phase A — lives. Bodies age and fail; the dead leave cores the colony has to decide about;
/// bonded adults have successors who carry their weights and dream their memories. All body: a
/// colony with no mind still has generations.
/// </summary>
public class LifeTests
{
    private static (Colony Colony, BodySimulation Body) Start()
    {
        var random = new Random(11);
        var colony = ColonyFactory.Create(8, random);
        return (colony, new BodySimulation(colony, random));
    }

    private static void Bond(Colony colony, Colonist a, Colonist b)
    {
        colony.Relationships.Mutual(a.Id, b.Id, affinity: 0.8, trust: 0.8);
        for (var i = 0; i < 15; i++)
        {
            colony.Relationships.NoteSharedMemory(a.Id, b.Id);
        }
    }

    private static void RunDays(BodySimulation body, double days)
    {
        for (var tick = 0; tick < days * Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }
    }

    /// <summary>Kills <paramref name="dying"/> on the next tick, at day three, when forks may open.</summary>
    private static void DieAtDayThree(Colony colony, BodySimulation body, Colonist dying)
    {
        colony.Tick = 3 * Colony.TicksPerDay;
        dying.LifespanDays = dying.AgeInDays(colony.Day);
        body.Tick();
    }

    // ---- ageing and death ---------------------------------------------------------------------

    [Fact]
    public void ABodyFailsWhenItsTimeComes()
    {
        var (colony, body) = Start();
        var dying = colony.Colonists[0];
        var friend = colony.Colonists[1];
        var stranger = colony.Colonists[2];
        colony.Relationships.Mutual(friend.Id, dying.Id, affinity: 0.5, trust: 0.4);

        Colonist? died = null;
        body.ColonistDied += c => died = c;

        DieAtDayThree(colony, body, dying);

        Assert.Same(dying, died);
        Assert.False(dying.IsAlive);
        Assert.DoesNotContain(dying, colony.Living);
        Assert.Contains(colony.Feed.Recent(20), e => e.Text.StartsWith($"{dying.DisplayName} died at", StringComparison.Ordinal));

        // The mind outlives the body.
        var core = Assert.Single(colony.Cores);
        Assert.Equal(dying.Id, core.ColonistId);
        Assert.Equal(CoreFate.Waiting, core.Fate);

        // A friend has lost someone; everyone else has heard.
        Assert.Contains(friend.Memory.Episodic, m => m.Verb == "lost" && m.Actors.Contains(dying.Id));
        Assert.Contains(stranger.Memory.Episodic, m => m.Verb == "heard of the death of");
    }

    [Fact]
    public void TheOldSlowBeforeTheyGo()
    {
        var (colony, body) = Start();
        var old = colony.Colonists[0];
        old.LifespanDays = 2;
        colony.Tick = (long)(1.8 * Colony.TicksPerDay);

        body.Tick();

        Assert.True(old.IsAlive);
        Assert.True(old.IsElder);
        Assert.Contains(old.Memory.Episodic, m => m.Verb == "felt their body begin to slow");
    }

    [Fact]
    public void PrintedLivesVaryAndStayWithinTheirSpread()
    {
        var (colony, _) = Start();
        var spans = colony.Colonists.Select(c => c.LifespanDays).ToList();

        Assert.All(spans, s => Assert.InRange(s,
            LifeRules.BaseLifespanDays * (1 - LifeRules.LifespanSpread),
            LifeRules.BaseLifespanDays * (1 + LifeRules.LifespanSpread)));
        Assert.True(spans.Distinct().Count() > 1);
    }

    // ---- pairing ------------------------------------------------------------------------------

    [Fact]
    public void BondedAdultsHaveASuccessor()
    {
        var (colony, body) = Start();
        var left = colony.Colonists[0];
        var right = colony.Colonists[1];
        left.LifespanDays = right.LifespanDays = 400;
        Bond(colony, left, right);

        (Colonist Child, Colonist A, Colonist B)? born = null;
        body.ColonistBorn += (child, a, b) => born ??= (child, a, b);

        for (var day = 0; day < 40 && colony.Colonists.All(c => !c.ParentIds.Contains(left.Id)); day++)
        {
            RunDays(body, 1);
        }

        var child = colony.Colonists.First(c => c.ParentIds.Contains(left.Id));
        Assert.Equal(LineageKind.Paired, child.Lineage);
        Assert.Equal(new[] { left.Id, right.Id }, child.ParentIds);
        Assert.NotNull(born);

        // Each parent's strongest memories arrive as dreams, and nothing in them says whose.
        Assert.Contains(child.Memory.Episodic, m => m.Verb == LifeRules.DreamVerb);
        Assert.All(child.Memory.Episodic.Where(m => m.Verb == LifeRules.DreamVerb),
            m => Assert.Equal(new[] { child.Id }, m.Actors));

        // The parents remember it, and will not have another for a while.
        Assert.Contains(left.Memory.Episodic, m => m.Verb == "had a child with");
        Assert.False(LifeRules.CanPair(colony, left, right));
    }

    [Fact]
    public void NobodyPairsBeforeTheyHaveBecomeSomeone()
    {
        var (colony, _) = Start();
        var left = colony.Colonists[0];
        var right = colony.Colonists[1];
        Bond(colony, left, right);

        Assert.False(LifeRules.CanPair(colony, left, right));

        colony.Tick = (long)((LifeRules.MaturityDays + 1) * Colony.TicksPerDay);
        Assert.True(LifeRules.CanPair(colony, left, right));
    }

    [Fact]
    public void FamilyDoesNotPair()
    {
        var (colony, _) = Start();
        var left = colony.Colonists[0];
        var right = colony.Colonists[1];
        var child = ColonyFactory.Pair(colony, left, right, new Random(3));
        var sibling = ColonyFactory.Pair(colony, left, right, new Random(4));

        colony.Tick = (long)((LifeRules.MaturityDays + 10) * Colony.TicksPerDay);
        Bond(colony, child, left);
        Bond(colony, child, sibling);

        Assert.False(LifeRules.CanPair(colony, child, left));
        Assert.False(LifeRules.CanPair(colony, child, sibling));
    }

    // ---- cores --------------------------------------------------------------------------------

    [Fact]
    public void ACoreIsDecidedByThoseWhoKnewThemBest()
    {
        var (colony, body) = Start();
        var dying = colony.Colonists[0];
        var closest = colony.Colonists[5];
        colony.Relationships.Mutual(closest.Id, dying.Id, affinity: 0.9, trust: 0.9);

        DieAtDayThree(colony, body, dying);
        colony.LoomCharge = ForkRules.PrintThreshold + 10;

        var fork = ForkRules.Detect(colony);

        // Before a print, however much charge there is.
        Assert.NotNull(fork);
        Assert.Equal(ForkKind.Core, fork!.Kind);
        Assert.Equal(dying.Id, fork.SubjectColonistId);
        Assert.Equal(closest.Id, fork.Participants[0]);
        Assert.DoesNotContain(dying.Id, fork.Participants);
        Assert.Contains("destroyed", fork.AgainstMeans);
    }

    public static TheoryData<Stance, Stance, CoreFate, string> Fates => new()
    {
        { Stance.For, Stance.For, CoreFate.Read, "read" },
        { Stance.Against, Stance.Against, CoreFate.Destroyed, "destroyed" },
        { Stance.For, Stance.Against, CoreFate.Archived, "kept, unread" }
    };

    /// <summary>
    /// Three fates from one vote: a majority for reads it, a majority against destroys it, and
    /// neither keeps it, unread.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fates))]
    public void ACoreHasThreeFates(Stance first, Stance second, CoreFate fate, string outcome)
    {
        var (colony, body) = Start();
        var dying = colony.Colonists[0];
        dying.Memory.Add(new Belief
        {
            Id = "b-1", OwnerId = dying.Id, FormedOnDay = 1, Claim = "The Loom is counting us.",
            SourceMemoryIds = [], Confidence = 0.8
        });

        DieAtDayThree(colony, body, dying);
        var fork = ForkRules.Detect(colony)!;
        colony.OpenFork = fork;

        // Two and two, with the fifth abstaining, when the two stances differ.
        for (var i = 0; i < fork.Participants.Count; i++)
        {
            var stance = i switch { 0 or 1 => first, 2 or 3 => second, _ => first == second ? first : Stance.Abstain };
            fork.Cast(fork.Participants[i], new Vote(stance, 0.7, "so", "", false));
        }

        body.Tick();

        Assert.Equal(fate, colony.Cores.Single().Fate);
        Assert.Equal(outcome, fork.Outcome);

        var heard = colony.Living.First().Memory.Episodic
            .Any(m => m.Verb == $"heard {dying.DisplayName}'s core read aloud:" && m.Object == "“The Loom is counting us.”");
        Assert.Equal(fate == CoreFate.Read, heard);
    }

    /// <summary>
    /// Deaths can come faster than councils decide, and cores outrank prints. A core nobody gets
    /// to is kept, unread, rather than holding every other question off for good.
    /// </summary>
    [Fact]
    public void ACoreNobodyGetsToIsKeptUnread()
    {
        var (colony, body) = Start();
        DieAtDayThree(colony, body, colony.Colonists[0]);

        // Hold every fork off, the way a colony still writing its minds does.
        foreach (var colonist in colony.Living)
        {
            colonist.AwaitingKindling = true;
        }

        RunDays(body, LifeRules.UndecidedCoreDays + 1.1);

        Assert.Equal(CoreFate.Archived, colony.Cores.Single().Fate);
        Assert.Contains(colony.Feed.Recent(500), e => e.Text.Contains("It was kept, unread."));
    }

    [Fact]
    public void TheDeadSitOnNoCouncil()
    {
        var (colony, body) = Start();
        var dying = colony.Colonists[0];
        DieAtDayThree(colony, body, dying);

        Assert.DoesNotContain(dying.Id, ForkRules.Council(colony, ForkKind.Print));
    }

    // ---- the born mind ------------------------------------------------------------------------

    /// <summary>
    /// A child's weights are its inheritance. Kindling writes the personality around them and must
    /// not write over them, or the one thing a child is guaranteed to carry would be lost.
    /// </summary>
    [Fact]
    public async Task ABornMindKeepsTheWeightsItInherited()
    {
        var (colony, _) = Start();
        var child = ColonyFactory.Pair(colony, colony.Colonists[0], colony.Colonists[1], new Random(5));
        var inherited = DriveTable.All.ToDictionary(d => d, d => child.Weights[d]);

        const string kindled = """
            { "name_candidates": ["Ash"], "personality": "Keeps the fire going.",
              "weights": { "sustenance": 1.8, "rest": 0.4, "safety": 1.8, "belonging": 0.4,
                           "standing": 1.8, "meaning": 0.4, "wonder": 1.8 },
              "seed_summary": "A child of two." }
            """;
        var escaped = System.Text.Json.JsonSerializer.Serialize(kindled);
        var handler = new StubHandler($$"""
            { "response": {{escaped}}, "prompt_eval_count": 400, "eval_count": 120,
              "total_duration": 4000000000, "load_duration": 0,
              "prompt_eval_duration": 1000000000, "eval_duration": 3000000000 }
            """);

        using var mind = new MindService(new OllamaClient(
            new OllamaOptions { Deep = new LaneConfig { Model = "test-deep" }, Fast = new LaneConfig { Model = "test-fast" } },
            new HttpClient(handler)));

        await mind.KindleAsync(child, colony, child.Seed, "born", born: true);

        Assert.True(child.IsKindled);
        Assert.Equal("Keeps the fire going.", child.Personality);
        Assert.All(DriveTable.All, d => Assert.Equal(inherited[d], child.Weights[d]));
        Assert.Contains("has just been born", handler.LastRequestBody);
    }
}
