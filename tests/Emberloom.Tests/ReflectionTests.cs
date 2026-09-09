using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Memory;
using Emberloom.Core.Simulation;
using Emberloom.Mind;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Ollama;
using Xunit;

namespace Emberloom.Tests;

public class ReflectionTests
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

    private static (Colony Colony, Colonist Colonist, List<EpisodicMemory> Batch) Scenario()
    {
        var random = new Random(5);
        var colony = ColonyFactory.Create(3, random);
        var colonist = colony.Colonists[0];

        // Deliberately four distinct acts. Identical rows fold into one now, which is correct
        // behaviour but makes for a fixture that does not resemble a real reflection batch.
        var rows = new (string Verb, string About, string Place)[]
        {
            ("worked at clearing", "The East Stairwell", "east-stair"),
            ("recovered an artifact at", "The Substation", "substation"),
            ("talked with", "C-02", "plaza"),
            ("was hurt at", "The Ninth Floor", "ninth-floor")
        };

        var batch = new List<EpisodicMemory>();

        for (var i = 0; i < rows.Length; i++)
        {
            var memory = new EpisodicMemory
            {
                Id = $"m-{i}",
                Tick = i * 10,
                Day = 1 + i * 0.1,
                Actors = [colonist.Id],
                Verb = rows[i].Verb,
                Object = rows[i].About,
                PlaceId = rows[i].Place,
                Valence = 0.4,
                Intensity = 0.8
            };

            colonist.Memory.Record(memory);
            batch.Add(memory);
        }

        return (colony, colonist, batch);
    }

    private const string GoodReflection = """
        {
          "consolidated": {
            "text": "Four days at the stairwell, alone, and it is still shut.",
            "valence": 0.2,
            "source_memory_ids": ["m-0", "m-1", "m-2", "m-3"]
          },
          "beliefs": [
            { "claim": "What the makers sealed, they sealed for a reason.",
              "source_memory_ids": ["m-0", "m-3"], "confidence": 0.7 }
          ],
          "relationship_note": { "about": "", "reading": "" },
          "weight_drift": { "sustenance": 0, "rest": 0, "safety": 0, "belonging": 0,
                            "standing": 0, "meaning": 0, "wonder": 0.08 }
        }
        """;

    [Fact]
    public async Task ABeliefCarriesTheMemoriesThatProducedIt()
    {
        var (colony, colonist, _) = Scenario();
        var handler = new StubHandler(Envelope(GoodReflection));
        using var mind = new MindService(new OllamaClient(Options(), new HttpClient(handler)));

        await mind.ReflectAsync(colonist, colony);

        var belief = Assert.Single(colonist.Memory.Beliefs);
        Assert.Equal("What the makers sealed, they sealed for a reason.", belief.Claim);
        Assert.Equal(["m-0", "m-3"], belief.SourceMemoryIds);

        // The whole point: every id resolves to a row that actually exists.
        foreach (var id in belief.SourceMemoryIds)
        {
            Assert.NotNull(colonist.Memory.FindEpisodic(id));
        }
    }

    /// <summary>
    /// A model that invents a citation produces a belief whose chain dead-ends. An untraceable
    /// belief is the one thing this design promises never to show, so it is dropped entirely
    /// rather than displayed with a broken link.
    /// </summary>
    [Fact]
    public async Task BeliefsCitingMemoriesThatDoNotExistAreDiscarded()
    {
        var (colony, colonist, _) = Scenario();

        var handler = new StubHandler(Envelope("""
            {
              "consolidated": { "text": "Days at the door.", "valence": 0.1,
                                "source_memory_ids": ["m-0"] },
              "beliefs": [
                { "claim": "Invented from nothing.", "source_memory_ids": ["m-999"], "confidence": 0.9 },
                { "claim": "Grounded in something real.", "source_memory_ids": ["m-1"], "confidence": 0.6 }
              ],
              "relationship_note": { "about": "", "reading": "" },
              "weight_drift": {}
            }
            """));

        using var mind = new MindService(new OllamaClient(Options(), new HttpClient(handler)));
        await mind.ReflectAsync(colonist, colony);

        var belief = Assert.Single(colonist.Memory.Beliefs);
        Assert.Equal("Grounded in something real.", belief.Claim);
    }

    [Fact]
    public async Task ReflectionConsumesTheBatchSoItDoesNotResurfaceForever()
    {
        var (colony, colonist, batch) = Scenario();
        var handler = new StubHandler(Envelope(GoodReflection));
        using var mind = new MindService(new OllamaClient(Options(), new HttpClient(handler)));

        await mind.ReflectAsync(colonist, colony);

        Assert.All(batch, m => Assert.True(m.Consolidated));
        Assert.False(colonist.Memory.WantsReflection(colony.Day));
    }

    [Fact]
    public async Task WeightDriftMovesTheBodyWithoutRewritingIt()
    {
        var (colony, colonist, _) = Scenario();
        var before = colonist.Weights[Drive.Wonder];

        var handler = new StubHandler(Envelope(GoodReflection));
        using var mind = new MindService(new OllamaClient(Options(), new HttpClient(handler)));

        await mind.ReflectAsync(colonist, colony);

        Assert.Equal(before + 0.08, colonist.Weights[Drive.Wonder], precision: 3);
    }

    /// <summary>
    /// With no mind, a colonist must not accumulate an unbounded backlog of experience waiting to
    /// be understood — and must visibly hold no convictions rather than convictions nobody wrote.
    /// </summary>
    [Fact]
    public async Task WithoutAMindTheBatchIsStillConsumedAndNoBeliefsAreInvented()
    {
        var (colony, colonist, batch) = Scenario();

        using var mind = new MindService(new OllamaOptions
        {
            Endpoint = new Uri("http://127.0.0.1:9"),
            Deep = new LaneConfig { Model = "absent" },
            Fast = new LaneConfig { Model = "absent" },
            RequestTimeout = TimeSpan.FromSeconds(2)
        });

        var result = await mind.ReflectAsync(colonist, colony);

        Assert.False(result.FromModel);
        Assert.Empty(colonist.Memory.Beliefs);
        Assert.All(batch, m => Assert.True(m.Consolidated));
        Assert.True(colonist.Trace.Single(t => t.CallType == "Reflection").WasFallback);
    }

    [Fact]
    public async Task ARelationshipReadingLandsOnTheRightColonist()
    {
        var (colony, colonist, _) = Scenario();
        var other = colony.Colonists[1];

        var handler = new StubHandler(Envelope($$"""
            {
              "consolidated": { "text": "About {{other.Designation}}.", "valence": 0.3,
                                "source_memory_ids": ["m-0"] },
              "beliefs": [],
              "relationship_note": { "about": "{{other.Designation}}",
                                     "reading": "Does the work and does not collect on it." },
              "weight_drift": {}
            }
            """));

        using var mind = new MindService(new OllamaClient(Options(), new HttpClient(handler)));
        await mind.ReflectAsync(colonist, colony);

        Assert.Equal(
            "Does the work and does not collect on it.",
            colony.Relationships.Get(colonist.Id, other.Id).Narrative);
    }
}

/// <summary>
/// Retrieval is the likeliest place in the design to break the golden rule, because moving the
/// index server-side looks like an optimisation. These tests pin the behaviour down locally.
/// </summary>
public class RetrievalTests
{
    private static float[] Vector(params float[] values) => values;

    private static MemoryStore StoreWith(params (string Text, float[] Embedding, double Day)[] rows)
    {
        var store = new MemoryStore();
        var index = 0;

        foreach (var (text, embedding, day) in rows)
        {
            store.Add(new ConsolidatedMemory
            {
                Id = $"cm-{index++}",
                OwnerId = "c01",
                Day = day,
                Text = text,
                Valence = 0.5,
                SourceMemoryIds = ["m-0"],
                Embedding = embedding
            });
        }

        return store;
    }

    [Fact]
    public void TheMostSimilarMemoryComesBackFirst()
    {
        var store = StoreWith(
            ("the door on the ninth floor", Vector(1, 0, 0), 10),
            ("foraging at the orchard", Vector(0, 1, 0), 10));

        var results = Retrieval.Search(store, Vector(1, 0, 0), currentDay: 11);

        Assert.Equal("the door on the ninth floor", results[0].Text);
    }

    [Fact]
    public void OlderMemoriesRankBelowEquallyRelevantRecentOnes()
    {
        var store = StoreWith(
            ("long ago", Vector(1, 0, 0), 1),
            ("yesterday", Vector(1, 0, 0), 60));

        var results = Retrieval.Search(store, Vector(1, 0, 0), currentDay: 61);

        Assert.Equal("yesterday", results[0].Text);
    }

    [Fact]
    public void BeliefsAreRetrievedAlongsideMemoriesAndFadeFarSlower()
    {
        var store = StoreWith(("an old day", Vector(1, 0, 0), 1));

        store.Add(new Belief
        {
            Id = "b-1",
            OwnerId = "c01",
            FormedOnDay = 1,
            Claim = "an old conviction",
            SourceMemoryIds = ["m-0"],
            Confidence = 0.9,
            Embedding = Vector(1, 0, 0)
        });

        var results = Retrieval.Search(store, Vector(1, 0, 0), currentDay: 90);

        // Holding a belief is the point of having formed it, so it should outlast the episode.
        Assert.True(results[0].IsBelief, "a belief should outrank an equally old, equally relevant memory");
    }

    [Fact]
    public void RecallMakesWhatAMindKeepsReturningToMoreAvailable()
    {
        var store = StoreWith(("the door", Vector(1, 0, 0), 10));

        Retrieval.Search(store, Vector(1, 0, 0), currentDay: 11);
        Retrieval.Search(store, Vector(1, 0, 0), currentDay: 11);

        Assert.Equal(2, store.Consolidated[0].RecallCount);
    }

    [Fact]
    public void RowsWithNoEmbeddingAreInvisibleRatherThanWrong()
    {
        var store = new MemoryStore();
        store.Add(new ConsolidatedMemory
        {
            Id = "cm-0",
            OwnerId = "c01",
            Day = 1,
            Text = "never embedded",
            Valence = 0,
            SourceMemoryIds = ["m-0"]
        });

        Assert.Empty(Retrieval.Search(store, Vector(1, 0, 0), currentDay: 2));
    }
}
