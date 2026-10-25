using System.Net;
using System.Text;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Simulation;
using Emberloom.Mind;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Ollama;
using Emberloom.Mind.Prompts;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// A stub transport, so the call types can be tested without a model. The point of these tests is
/// the seam — what a kindling result does to a colonist — not whether Ollama works.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _body = body;
        _status = status;
    }

    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json")
        };
    }
}

public class KindlingTests
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
            { "response": {{escaped}}, "prompt_eval_count": 120, "eval_count": 260,
              "total_duration": 5000000000, "load_duration": 0,
              "prompt_eval_duration": 1000000000, "eval_duration": 4000000000 }
            """;
    }

    private const string GoodKindling = """
        {
          "name_candidates": ["Vigil", "Sentry"],
          "personality": "Moves deliberately. Pauses at the threshold of every decision. Speaks only when the words are measured.",
          "weights": { "sustenance": 1.0, "rest": 1.1, "safety": 1.6, "belonging": 0.9,
                       "standing": 0.6, "meaning": 1.3, "wonder": 0.5 },
          "seed_summary": "careful, and slow to be moved"
        }
        """;

    [Fact]
    public async Task KindlingProducesWeightsTheBodyCanScoreWith()
    {
        var handler = new StubHandler(Envelope(GoodKindling));
        using var client = new OllamaClient(Options(), new HttpClient(handler));

        var result = await KindlingCall.ExecuteAsync(client, new KindlingSeed
        {
            Designation = "C-09",
            Name = "Vael",
            Seed = "steady, watches before acting"
        });

        Assert.True(result.FromModel);
        Assert.Equal(["Vigil", "Sentry"], result.NameCandidates);
        Assert.Equal(1.6, result.Weights[Drive.Safety], precision: 3);
        Assert.Equal(0.5, result.Weights[Drive.Wonder], precision: 3);
    }

    /// <summary>
    /// The prompt has to be self-contained: everything the call needs travels with it, and the
    /// server is told nothing about the colony. That property is the whole reason a remote
    /// inference endpoint stays a config change rather than a rewrite, so it is worth a test.
    /// </summary>
    [Fact]
    public async Task TheCallCarriesItsOwnContextAndAsksTheServerToRememberNothing()
    {
        var handler = new StubHandler(Envelope(GoodKindling));
        using var client = new OllamaClient(Options(), new HttpClient(handler));

        await KindlingCall.ExecuteAsync(client, new KindlingSeed
        {
            Designation = "C-09",
            Name = "Vael",
            Seed = "steady, watches before acting"
        });

        var sent = handler.LastRequestBody!;

        // World rules and the seed both travel in the request itself.
        Assert.Contains("A machine called the Loom", sent);
        Assert.Contains("steady, watches before acting", sent);

        // And nothing else does. The check is on the request's shape rather than its prose,
        // because the prompt legitimately talks about conversations — what matters is that there
        // is no handle the server would have to remember anything against.
        using var document = System.Text.Json.JsonDocument.Parse(sent);
        var fields = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        Assert.Equal(
            new HashSet<string> { "model", "system", "prompt", "stream", "keep_alive", "think", "format", "options" },
            fields);
    }

    [Fact]
    public async Task ReasoningIsDisabledSoTheTokenCapBuysTheAnswer()
    {
        // M0 finding: on a thinking-capable model the cap is spent on reasoning that is returned
        // separately, and every schema call comes back truncated or empty.
        var handler = new StubHandler(Envelope(GoodKindling));
        using var client = new OllamaClient(Options(), new HttpClient(handler));

        await KindlingCall.ExecuteAsync(client, new KindlingSeed { Designation = "C-09", Name = "Vael", Seed = "x" });

        Assert.Contains("\"think\":false", handler.LastRequestBody!.Replace(" ", string.Empty));
    }

    [Fact]
    public async Task TruncatedOutputFallsBackRatherThanProducingAHalfPerson()
    {
        // Constrained decoding that runs out of budget leaves a document shaped right and empty.
        var handler = new StubHandler(Envelope("""{ "name_candidates": [], "personality": "" }"""));
        using var client = new OllamaClient(Options(), new HttpClient(handler));

        var result = await KindlingCall.ExecuteAsync(client, new KindlingSeed
        {
            Designation = "C-09",
            Name = "Vael",
            Seed = "steady"
        });

        Assert.False(result.FromModel);
        Assert.NotEmpty(result.Personality);
    }

    [Fact]
    public void TheFallbackIsStableForTheSameColonist()
    {
        // A colonist must not change identity if the mind comes back later.
        var seed = new KindlingSeed { Designation = "C-09", Name = "Vael", Seed = "steady" };

        var first = KindlingCall.Fallback(seed);
        var second = KindlingCall.Fallback(seed);

        Assert.Equal(first.Personality, second.Personality);
        Assert.Equal(first.Weights[Drive.Wonder], second.Weights[Drive.Wonder], precision: 6);
    }

    /// <summary>
    /// The architecture's headline promise, now with a mind attached: an unreachable Ollama must
    /// leave a colonist who still exists, still has weights, and still acts.
    /// </summary>
    [Fact]
    public async Task AnUnreachableMindStillProducesAColonistWhoActs()
    {
        var random = new Random(3);
        var colony = ColonyFactory.Create(4, random);
        var body = new BodySimulation(colony, random);
        var colonist = colony.Colonists[0];

        // Nothing is listening on this port.
        using var mind = new MindService(new OllamaOptions
        {
            Endpoint = new Uri("http://127.0.0.1:9"),
            Deep = new LaneConfig { Model = "absent" },
            Fast = new LaneConfig { Model = "absent" },
            RequestTimeout = TimeSpan.FromSeconds(2)
        });

        var result = await mind.KindleAsync(colonist, colony, colonist.Seed);

        Assert.False(result.FromModel);
        Assert.False(colonist.IsKindled);
        Assert.NotEmpty(colonist.Personality);

        // The trace has to say plainly that this was a stub, or a colony running entirely on
        // fallbacks would look like a colony that thought.
        var trace = Assert.Single(colonist.Trace);
        Assert.True(trace.WasFallback);

        for (var i = 0; i < Colony.TicksPerDay * 3; i++)
        {
            body.Tick();
        }

        Assert.True(colonist.Memory.Episodic.Count > 0);
    }

    [Fact]
    public async Task KindlingRewritesTheWeightsTheBodyAlreadyHad()
    {
        var random = new Random(3);
        var colony = ColonyFactory.Create(2, random);
        var colonist = colony.Colonists[0];
        var before = colonist.Weights[Drive.Safety];

        var handler = new StubHandler(Envelope(GoodKindling));
        using var client = new OllamaClient(Options(), new HttpClient(handler));
        using var mind = new MindService(client);

        await mind.KindleAsync(colonist, colony, "steady, watches before acting");

        Assert.True(colonist.IsKindled);
        Assert.Equal(1.6, colonist.Weights[Drive.Safety], precision: 3);
        Assert.NotEqual(before, colonist.Weights[Drive.Safety]);
        Assert.Contains("Moves deliberately", colonist.Personality);
    }
}

public class PromptTests
{
    [Fact]
    public void TheStablePrefixComesFirstSoTheCacheCanReuseIt()
    {
        var random = new Random(1);
        var colony = ColonyFactory.Create(2, random);
        var system = WorldPrompt.SystemFor(colony.Colonists[0], colony);

        var world = system.IndexOf("[World]", StringComparison.Ordinal);
        var colonist = system.IndexOf("[Colonist]", StringComparison.Ordinal);

        Assert.Equal(0, world);
        Assert.True(colonist > world, "the per-colonist block must follow the world rules, not precede them");
    }

    [Fact]
    public void OnlyNotableWeightsAreSpelledOut()
    {
        // A wall of "1.00" for ordinary drives teaches the model nothing and costs prompt tokens.
        var weights = PersonalityWeights.From((Drive.Wonder, 1.6), (Drive.Safety, 0.7));
        var text = WorldPrompt.Weights(weights);

        Assert.Contains("wonder 1.6x", text);
        Assert.Contains("safety 0.7x", text);
        Assert.DoesNotContain("sustenance", text);
    }

    [Fact]
    public void AColonistWithNoStrongLeaningsStillReadsAsSomeone()
    {
        Assert.Contains("nothing pulls harder", WorldPrompt.Weights(new PersonalityWeights()));
    }
}

public class SeedRollerTests
{
    [Fact]
    public void RolledSeedsVaryRatherThanRepeatingTheSameEightPeople()
    {
        var random = new Random(9);
        var seeds = Enumerable.Range(0, 24).Select(_ => SeedRoller.Roll(random)).ToHashSet();

        Assert.True(seeds.Count > 18, $"only {seeds.Count} distinct seeds in 24 rolls");
    }
}

public class KindlingPromptTests
{
    /// <summary>
    /// Naming the drives is not enough — the model has to be told what they mean in this world.
    /// Without the glossary, Kindling put wonder at the floor for most colonists, including one
    /// whose seed said it was drawn to the parts of the city nobody had opened.
    /// </summary>
    [Fact]
    public void ThePromptExplainsWhatEachDriveMeans()
    {
        var prompt = KindlingCall.BuildPrompt(new KindlingSeed
        {
            Designation = "C-09",
            Name = "Vael",
            Seed = "drawn to the parts of the city nobody has opened"
        });

        foreach (var drive in DriveTable.All)
        {
            Assert.Contains(DriveTable.Describe(drive), prompt, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("unopened doors", prompt);
        Assert.Contains("the forest is generous", prompt);
    }

    [Fact]
    public void ThePromptAsksForARestrainedSpreadRatherThanEverythingAtOnce()
    {
        var prompt = KindlingCall.BuildPrompt(new KindlingSeed { Designation = "C-09", Name = "Vael", Seed = "steady" });

        Assert.Contains("1.4 to 1.8", prompt);
        Assert.Contains("0.4 to 0.7", prompt);
    }
    /// <summary>
    /// A call's seconds are not a measure of how fast the model is, and the trace has to carry the
    /// split that says so.
    /// <para>
    /// Measured 24 October 2026, chasing a vote that took four minutes: a deliberation's recorded
    /// rate was 1.06 tokens a second while the same model, same prompt and same schema generated at
    /// 9.4. The difference was entirely time the request spent queued inside the inference server.
    /// Completion tokens over total seconds therefore measures congestion and reports it as speed,
    /// which is how one perfectly healthy call type came to look six times slower than its
    /// neighbours — and how most of an evening went into chasing it.
    /// </para>
    /// </summary>
    [Fact]
    public void ACallsTimeIsBrokenDownSoItsSpeedCanBeToldFromItsWaiting()
    {
        var record = new ModelCallRecord
        {
            CallType = "Deliberation",
            Day = 2.0,
            Model = "llama3.2:3b",
            Lane = "deep",
            PromptTokens = 671,
            CompletionTokens = 82,
            Seconds = 77.5,
            LoadSeconds = 0,
            PrefillSeconds = 11.9,
            GenerateSeconds = 8.7,
            Produced = "voted for",
        };

        // What is left is what the call spent waiting its turn — most of it, in the case that
        // prompted this.
        Assert.Equal(56.9, record.WaitingSeconds, 1);

        // The honest rate, and the misleading one that reading Seconds alone would give.
        Assert.Equal(9.4, record.CompletionTokens / record.GenerateSeconds, 1);
        Assert.Equal(1.1, record.CompletionTokens / record.Seconds, 1);
    }

    /// <summary>A breakdown adding up to more than the total cannot make the waiting negative.</summary>
    [Fact]
    public void WaitingNeverGoesNegativeWhenTheServerRoundsAgainstUs()
    {
        var record = new ModelCallRecord
        {
            CallType = "IdleThought",
            Day = 1.0,
            Model = "llama3.2:1b",
            Lane = "fast",
            PromptTokens = 394,
            CompletionTokens = 24,
            Seconds = 2.2,
            PrefillSeconds = 1.4,
            GenerateSeconds = 1.1,
            Produced = "thought something",
        };

        Assert.Equal(0, record.WaitingSeconds);
    }

}
