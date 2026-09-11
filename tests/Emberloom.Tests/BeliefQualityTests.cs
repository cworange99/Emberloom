using Emberloom.Api;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Memory;
using Emberloom.Core.Simulation;
using Emberloom.Mind;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Ollama;
using Emberloom.Mind.Prompts;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// What the inspector showed in a 47-day laptop colony, pinned down: beliefs repeated inside one
/// mind, raw prompt figures handed back as convictions, and a Contemplation readout that trusted a
/// benchmark instead of the machine.
/// </summary>
public class BeliefQualityTests
{
    private static OllamaOptions Options() => new()
    {
        Deep = new LaneConfig { Model = "test-deep" },
        Fast = new LaneConfig { Model = "test-fast" }
    };

    private static string Envelope(string innerJson)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(innerJson);
        return $$"""
            { "response": {{escaped}}, "prompt_eval_count": 300, "eval_count": 380,
              "total_duration": 7000000000, "load_duration": 0,
              "prompt_eval_duration": 1000000000, "eval_duration": 6000000000 }
            """;
    }

    private static string ReflectionClaiming(string claim, params string[] sources)
    {
        var ids = string.Join(", ", sources.Select(s => $"\"{s}\""));
        return $$"""
            {
              "consolidated": { "text": "Days at the door.", "valence": 0.2, "source_memory_ids": [{{ids}}] },
              "beliefs": [ { "claim": "{{claim}}", "source_memory_ids": [{{ids}}], "confidence": 0.6 } ],
              "relationship_note": { "about": "", "reading": "" },
              "weight_drift": {}
            }
            """;
    }

    private static MindService MindAnswering(string reflection) =>
        new(new OllamaClient(Options(), new HttpClient(new StubHandler(Envelope(reflection)))));

    private static void Remember(Colonist colonist, int from, int count)
    {
        var acts = new (string Verb, string About, string Place, double Valence)[]
        {
            ("worked at clearing", "The East Stairwell", "east-stair", 0.15),
            ("recovered an artifact at", "The Substation", "substation", 0.75),
            ("talked with", "C-02", "plaza", 0.3),
            ("was hurt at", "The Ninth Floor", "ninth-floor", -0.7)
        };

        for (var i = from; i < from + count; i++)
        {
            var act = acts[i % acts.Length];
            colonist.Memory.Record(new EpisodicMemory
            {
                Id = $"m-{i}",
                Tick = i * 10,
                Day = 1 + i * 0.1,
                Actors = [colonist.Id],
                Verb = act.Verb,
                Object = $"{act.About} {i}",
                PlaceId = act.Place,
                Valence = act.Valence,
                Intensity = 0.8
            });
        }
    }

    private static (Colony Colony, Colonist Colonist) Scenario()
    {
        var colony = ColonyFactory.Create(3, new Random(5));
        var colonist = colony.Colonists[0];
        Remember(colonist, 0, 4);
        return (colony, colonist);
    }

    [Fact]
    public async Task ARestatedBeliefReinforcesTheOneAlreadyHeld()
    {
        var (colony, colonist) = Scenario();

        using (var mind = MindAnswering(ReflectionClaiming("What the makers sealed, they sealed for a reason.", "m-0", "m-3")))
        {
            await mind.ReflectAsync(colonist, colony);
        }

        var first = Assert.Single(colonist.Memory.Beliefs);

        Remember(colonist, 4, 4);

        using (var mind = MindAnswering(ReflectionClaiming("What the makers sealed, they sealed for good reason.", "m-4", "m-5")))
        {
            await mind.ReflectAsync(colonist, colony);
        }

        // Still one conviction, in its original words and under its original id, so every chain
        // that already pointed at it still resolves.
        var belief = Assert.Single(colonist.Memory.Beliefs);
        Assert.Equal(first.Id, belief.Id);
        Assert.Equal(first.Claim, belief.Claim);

        // Stronger for having been arrived at again, and now citing both batches.
        Assert.True(belief.Confidence > first.Confidence);
        foreach (var id in new[] { "m-0", "m-3", "m-4", "m-5" })
        {
            Assert.Contains(id, belief.SourceMemoryIds);
        }

        // And the call that reinforced it is on record, and links back to it.
        var reinforcing = colonist.FindCall(Assert.Single(belief.ReinforcedByCallIds));
        Assert.NotNull(reinforcing);
        Assert.Contains(belief.Id, reinforcing!.ProducedIds);
        Assert.Contains("reinforced", reinforcing.Produced);
    }

    [Fact]
    public async Task ADifferentBeliefIsKeptAsItsOwn()
    {
        var (colony, colonist) = Scenario();

        using (var mind = MindAnswering(ReflectionClaiming("What the makers sealed, they sealed for a reason.", "m-0")))
        {
            await mind.ReflectAsync(colonist, colony);
        }

        Remember(colonist, 4, 4);

        using (var mind = MindAnswering(ReflectionClaiming("The forest gives more than it takes.", "m-4")))
        {
            await mind.ReflectAsync(colonist, colony);
        }

        Assert.Equal(2, colonist.Memory.Beliefs.Count);
        Assert.All(colonist.Memory.Beliefs, b => Assert.Empty(b.ReinforcedByCallIds));
    }

    [Theory]
    [InlineData("I am useful.", "My usefulness is proven.", false)]
    [InlineData("I can make a positive impact on the community.", "I am capable of making a positive impact on the community.", true)]
    [InlineData("The Loom is reliable.", "The forest is generous.", false)]
    public void WordOverlapCatchesRestatementsWithoutMergingDifferentClaims(string left, string right, bool restates)
    {
        var a = new Belief { Id = "b-1", OwnerId = "c01", FormedOnDay = 1, Claim = left, SourceMemoryIds = ["m-0"], Confidence = 0.5 };
        var b = new Belief { Id = "b-2", OwnerId = "c01", FormedOnDay = 2, Claim = right, SourceMemoryIds = ["m-1"], Confidence = 0.5 };

        // Word overlap is only the fallback, for when there are no embeddings. "I am useful" and
        // "my usefulness is proven" share one content word of two, and their embeddings sit at 0.85
        // among genuinely different beliefs, so both measures leave them apart — on purpose; see
        // BeliefSimilarity.EmbeddingThreshold.
        Assert.Equal(restates, BeliefSimilarity.Restates(a, b));
    }

    [Theory]
    [InlineData(0.97, true)]
    [InlineData(0.93, true)]
    [InlineData(0.88, false)]
    public void EmbeddingsDecideWhenBothClaimsHaveThem(double cosine, bool restates)
    {
        // Two unit vectors at a known angle. The claims share no words at all, so only the
        // embeddings can be what decides.
        var angle = Math.Acos(cosine);
        var a = new Belief
        {
            Id = "b-1", OwnerId = "c01", FormedOnDay = 1, Claim = "alpha", SourceMemoryIds = ["m-0"],
            Confidence = 0.5, Embedding = [1f, 0f]
        };
        var b = new Belief
        {
            Id = "b-2", OwnerId = "c01", FormedOnDay = 2, Claim = "omega", SourceMemoryIds = ["m-1"],
            Confidence = 0.5, Embedding = [(float)Math.Cos(angle), (float)Math.Sin(angle)]
        };

        Assert.Equal(restates, BeliefSimilarity.Restates(a, b));
    }

    [Fact]
    public void TheReflectionPromptCarriesNoRawNumbers()
    {
        var (_, colonist) = Scenario();
        var batch = colonist.Memory.ReflectionBatch(currentDay: 2);

        var prompt = ReflectionCall.BuildPrompt(colonist, batch);

        Assert.DoesNotMatch(@"\d+\.\d+", prompt);
        Assert.DoesNotContain("valence", prompt, StringComparison.OrdinalIgnoreCase);

        // How each thing felt is still there, as words.
        Assert.Contains("it went very badly", prompt);
    }

    [Fact]
    public void TheSystemPromptPutsWeightsInWords()
    {
        var (colony, colonist) = Scenario();
        colonist.Weights.Drift(Drive.Safety, 0.2);
        colonist.Weights.Drift(Drive.Safety, 0.2);
        colonist.Weights.Drift(Drive.Safety, 0.2);

        var system = WorldPrompt.SystemFor(colonist, colony);

        Assert.DoesNotMatch(@"\d+\.\d+", system);
        Assert.Contains("safety", system);
    }

    [Fact]
    public void ThroughputIsMeasuredOnlyOnceEnoughCallsHaveBeenServed()
    {
        var meter = new ThroughputMeter(window: 12, minimumSamples: 3);

        meter.Record(TimeSpan.FromSeconds(60));
        meter.Record(TimeSpan.FromSeconds(60));
        Assert.Null(meter.CallsPerMinute);

        meter.Record(TimeSpan.FromSeconds(60));
        Assert.Equal(1.0, meter.CallsPerMinute!.Value, precision: 3);
    }

    [Fact]
    public void ThroughputFollowsTheRecentCallsRatherThanTheWholeHistory()
    {
        var meter = new ThroughputMeter(window: 3, minimumSamples: 3);

        foreach (var seconds in new[] { 60, 60, 60, 30, 30, 30 })
        {
            meter.Record(TimeSpan.FromSeconds(seconds));
        }

        Assert.Equal(2.0, meter.CallsPerMinute!.Value, precision: 3);
    }
}
