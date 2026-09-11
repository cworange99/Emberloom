using Emberloom.Core.Colonists;
using Emberloom.Core.Memory;
using Emberloom.Core.Simulation;
using Emberloom.Mind;
using Emberloom.Mind.Ollama;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M4's promise: every chain in the inspector can be walked end to end. A belief leads to the
/// memories it came from and to the call that formed it; the call leads to exactly what was sent
/// and what came back; and every call the mind makes is in the trace.
/// </summary>
public class InspectorTests
{
    private static OllamaOptions Options() => new()
    {
        Deep = new LaneConfig { Model = "test-deep" },
        Fast = new LaneConfig { Model = "test-fast" }
    };

    private static OllamaOptions Unreachable() => new()
    {
        Endpoint = new Uri("http://127.0.0.1:9"),
        Deep = new LaneConfig { Model = "absent" },
        Fast = new LaneConfig { Model = "absent" },
        RequestTimeout = TimeSpan.FromSeconds(2)
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

    private const string Reflection = """
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
                            "standing": 0, "meaning": 0, "wonder": 0 }
        }
        """;

    private static (Colony Colony, Colonist Colonist, List<EpisodicMemory> Batch) Scenario()
    {
        var colony = ColonyFactory.Create(3, new Random(5));
        var colonist = colony.Colonists[0];

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

    [Fact]
    public async Task ABeliefNamesTheCallThatFormedIt()
    {
        var (colony, colonist, _) = Scenario();
        using var mind = new MindService(new OllamaClient(Options(), new HttpClient(new StubHandler(Envelope(Reflection)))));

        await mind.ReflectAsync(colonist, colony);

        var belief = Assert.Single(colonist.Memory.Beliefs);
        var consolidated = Assert.Single(colonist.Memory.Consolidated);

        Assert.NotNull(belief.FormedByCallId);
        Assert.Equal(belief.FormedByCallId, consolidated.FormedByCallId);

        // The link resolves, and it resolves both ways: the call names what it produced.
        var call = colonist.FindCall(belief.FormedByCallId!);
        Assert.NotNull(call);
        Assert.Equal("Reflection", call!.CallType);
        Assert.Contains(belief.Id, call.ProducedIds);
        Assert.Contains(consolidated.Id, call.ProducedIds);
    }

    [Fact]
    public async Task TheTraceKeepsWhatWasSentAndWhatCameBack()
    {
        var (colony, colonist, _) = Scenario();
        using var mind = new MindService(new OllamaClient(Options(), new HttpClient(new StubHandler(Envelope(Reflection)))));

        await mind.ReflectAsync(colonist, colony);

        var call = Assert.Single(colonist.Trace);
        Assert.True(call.HasText);
        Assert.False(string.IsNullOrWhiteSpace(call.System));

        // The batch the model was asked about is in the prompt it was sent, and the answer it
        // gave is the response — the two ends of the call, as they actually were.
        Assert.Contains("m-0", call.Prompt);
        Assert.Contains("What the makers sealed, they sealed for a reason.", call.Response);
    }

    [Fact]
    public async Task AnIdleThoughtIsACallAndGoesInTheTrace()
    {
        var (colony, colonist, _) = Scenario();
        var handler = new StubHandler(Envelope("""{ "thought": "The stairwell again. Always the stairwell." }"""));
        using var mind = new MindService(new OllamaClient(Options(), new HttpClient(handler)));

        await mind.ThinkAsync(colonist, colony);

        var call = Assert.Single(colonist.Trace);
        Assert.Equal("IdleThought", call.CallType);
        Assert.Equal("fast", call.Lane);
        Assert.Contains("The stairwell again.", call.Produced);
        Assert.Equal("The stairwell again. Always the stairwell.", colonist.LastThought);
    }

    /// <summary>
    /// Idle thought has no fallback — nothing durable was going to come of it. So with no mind it
    /// is silence, and a silent call that never happened must not appear as a line in the trace.
    /// </summary>
    [Fact]
    public async Task AThoughtThatNeverArrivedLeavesNoLine()
    {
        var (colony, colonist, _) = Scenario();
        using var mind = new MindService(Unreachable());

        await mind.ThinkAsync(colonist, colony);

        Assert.Empty(colonist.Trace);
        Assert.Null(colonist.LastThought);
    }

    [Fact]
    public void OnlyTheMostRecentCallsKeepTheirText()
    {
        var colony = ColonyFactory.Create(1, new Random(1));
        var colonist = colony.Colonists[0];
        const int Extra = 6;

        for (var i = 0; i < Colonist.TraceTextRetention + Extra; i++)
        {
            colonist.RecordCall(new ModelCallRecord
            {
                CallType = "IdleThought",
                Day = i,
                Model = "test-fast",
                Lane = "fast",
                PromptTokens = 10,
                CompletionTokens = 10,
                Seconds = 0.1,
                Produced = "a thought",
                Prompt = "prompt",
                Response = "response"
            });
        }

        var trace = colonist.Trace;

        // Measurements are kept for every call; only the text ages out.
        Assert.Equal(Colonist.TraceTextRetention + Extra, trace.Count);
        Assert.All(trace.Take(Extra), t => Assert.False(t.HasText));
        Assert.All(trace.Skip(Extra), t => Assert.True(t.HasText));

        // And every call gets its own id, so links into the trace are never ambiguous.
        Assert.Equal(trace.Count, trace.Select(t => t.Id).Distinct().Count());
    }

    /// <summary>
    /// The host runs every write-back under the lock the tick loop and the API use. The proof that
    /// nothing bypasses it: a commit that refuses to run anything leaves the colonist untouched —
    /// no belief, no consolidated memory, no trace line, no batch marked as understood.
    /// </summary>
    [Fact]
    public async Task EverythingTheMindWritesGoesThroughTheHostsCommit()
    {
        var (colony, colonist, batch) = Scenario();

        using var mind = new MindService(new OllamaClient(Options(), new HttpClient(new StubHandler(Envelope(Reflection)))))
        {
            Commit = _ => { }
        };

        await mind.ReflectAsync(colonist, colony);
        await mind.ThinkAsync(colonist, colony);

        Assert.Empty(colonist.Memory.Beliefs);
        Assert.Empty(colonist.Memory.Consolidated);
        Assert.Empty(colonist.Trace);
        Assert.All(batch, m => Assert.False(m.Consolidated));
    }

    /// <summary>
    /// A memory of time spent with someone names them by id, not only in its text. The id is what
    /// lets the inspector open the other mind from the far end of a chain — and a name can change
    /// once colonists start earning them, where an id cannot.
    /// </summary>
    [Fact]
    public void AMemoryOfSomeoneElseNamesThemById()
    {
        var random = new Random(4);
        var colony = ColonyFactory.Create(8, random);
        var body = new BodySimulation(colony, random);

        EpisodicMemory? shared = null;

        for (var tick = 0; tick < Colony.TicksPerDay * 20 && shared is null; tick++)
        {
            body.Tick();
            shared = colony.Colonists
                .SelectMany(c => c.Memory.Episodic)
                .FirstOrDefault(m => m.Verb is "taught" or "was taught by" or "talked with");
        }

        Assert.NotNull(shared);
        Assert.Equal(2, shared!.Actors.Count);
        Assert.NotEqual(shared.Actors[0], shared.Actors[1]);
        Assert.NotNull(colony.Find(shared.Actors[1]));
    }
}
