using Emberloom.Core.Colonists;
using Emberloom.Core.Culture;
using Emberloom.Core.Events;
using Emberloom.Core.Forks;
using Emberloom.Core.Memory;
using Emberloom.Core.Simulation;
using Emberloom.Core.Verbs;
using Emberloom.Mind;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Ollama;
using Emberloom.Mind.Prompts;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M7 phase B — culture. Colonists earn names; the colony coins words and tells itself a story
/// about its makers. Words and story go into every prompt, kept here and sent in full each time.
/// </summary>
public class CultureTests
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
            { "response": {{escaped}}, "prompt_eval_count": 500, "eval_count": 80,
              "total_duration": 4000000000, "load_duration": 0,
              "prompt_eval_duration": 1000000000, "eval_duration": 3000000000 }
            """;
    }

    private const string Coined = """{ "name": "the humming slate", "rationale": "Found where the Loom's cables go into the ground." }""";

    private const string Told = """
        { "story": "The makers built the Loom to remember them, and left when it began to remember too much. It prints us so that someone is here when they come back.",
          "contradicted_by": ["b-doubt", "b-nobody-holds-this"] }
        """;

    private static (Colony Colony, BodySimulation Body) Start()
    {
        var random = new Random(11);
        var colony = ColonyFactory.Create(8, random);
        return (colony, new BodySimulation(colony, random));
    }

    private static MindService Answering(string json, out StubHandler handler)
    {
        handler = new StubHandler(Envelope(json));
        return new MindService(new OllamaClient(Options(), new HttpClient(handler)));
    }

    // ---- names --------------------------------------------------------------------------------

    [Fact]
    public void ADeedEarnsTheFirstNameNobodyLivingHas()
    {
        var (colony, _) = Start();
        var first = colony.Colonists[0];
        var second = colony.Colonists[1];
        first.NameCandidates = ["Ash", "Reed"];
        second.NameCandidates = ["Ash", "Flint"];

        Assert.Equal("Ash", CultureRules.TakeAName(colony, first));
        Assert.Equal("Flint", CultureRules.TakeAName(colony, second));

        // Once named, a colonist keeps the name.
        Assert.Null(CultureRules.TakeAName(colony, first));
        Assert.Equal("Ash", first.DisplayName);
    }

    [Fact]
    public void AMindNobodyWroteKeepsItsDesignation()
    {
        var (colony, _) = Start();
        var unwritten = colony.Colonists[0];

        Assert.Null(CultureRules.TakeAName(colony, unwritten));
        Assert.Equal(unwritten.Designation, unwritten.DisplayName);
    }

    // ---- rituals and words --------------------------------------------------------------------

    [Fact]
    public void APracticeDoneOftenEnoughIsOfferedForAWordOnce()
    {
        var (colony, body) = Start();
        var keeper = colony.Colonists[0];
        keeper.Memory.Record(new EpisodicMemory
        {
            Id = "m-tend", Actors = [keeper.Id], Verb = "tended", Object = "the Loom",
            PlaceId = "loom", Valence = 0.3, Intensity = 0.4, Repeats = CultureRules.RitualRepeats - 1
        });

        var offered = new List<Occasion>();
        body.Coinable += offered.Add;

        for (var tick = 0; tick < 2 * Colony.TicksPerDay; tick++)
        {
            body.Tick();
        }

        var ritual = Assert.Single(offered, o => o.Kind == OccasionKind.Ritual && o.About == "tended the Loom");
        Assert.Equal(keeper.Id, ritual.ColonistId);
    }

    /// <summary>The golden rule, for Coinage: the whole context in, a word out, nothing remembered.</summary>
    [Fact]
    public async Task ACoinageCarriesItsOwnContextAndAsksTheServerToRememberNothing()
    {
        var (colony, _) = Start();
        var finder = colony.Colonists[0];
        var handler = new StubHandler(Envelope(Coined));
        using var client = new OllamaClient(Options(), new HttpClient(handler));

        await CoinageCall.ExecuteAsync(client, finder, colony, new Occasion(OccasionKind.Artifact, finder.Id, "The Market Hall", "market-hall"));

        var sent = handler.LastRequestBody!;
        Assert.Contains("A machine called the Loom", sent);
        Assert.Contains("The Market Hall", sent);

        using var document = System.Text.Json.JsonDocument.Parse(sent);
        Assert.Equal(
            new HashSet<string> { "model", "system", "prompt", "stream", "keep_alive", "think", "format", "options" },
            document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet());
    }

    [Fact]
    public async Task ACoinedWordGoesIntoEveryPromptAfterwards()
    {
        var (colony, _) = Start();
        var finder = colony.Colonists[0];
        using var mind = Answering(Coined, out _);

        await mind.CoinAsync(finder, colony, new Occasion(OccasionKind.Artifact, finder.Id, "The Market Hall", "market-hall"));

        var word = Assert.Single(colony.Lexicon);
        Assert.Equal("the humming slate", word.Term);
        Assert.Equal(finder.Id, word.CoinedById);
        Assert.Equal("Coinage", finder.FindCall(word.CallId!)!.CallType);

        // Everyone's, not just the coiner's.
        Assert.Contains("the humming slate", WorldPrompt.SystemFor(colony.Colonists[5], colony));

        // And not coined twice.
        await mind.CoinAsync(finder, colony, new Occasion(OccasionKind.Artifact, finder.Id, "The Substation", "substation"));
        Assert.Single(colony.Lexicon);
    }

    /// <summary>
    /// Live at M7 the colony coined "Remnant", then "maker's fragment", for the same kind of thing.
    /// The first find names the kind; every later find names the piece.
    /// </summary>
    [Fact]
    public async Task TheFirstFindNamesTheKindAndLaterFindsNameThePiece()
    {
        var (colony, _) = Start();
        var finder = colony.Colonists[0];
        var find = new Occasion(OccasionKind.Artifact, finder.Id, "The Market Hall", "market-hall");

        Assert.Contains("this kind of thing", CoinageCall.BuildPrompt(finder, colony, find));

        using var mind = Answering(Coined, out _);
        await mind.CoinAsync(finder, colony, find);

        var later = CoinageCall.BuildPrompt(finder, colony, find);
        Assert.Contains("The colony calls such things “the humming slate”", later);
        Assert.Contains("a name for this one", later);
        Assert.DoesNotContain("this kind of thing", later);
    }

    [Fact]
    public void ANameIsWrittenAsAName()
    {
        var (colony, _) = Start();
        var finder = colony.Colonists[0];
        finder.NameCandidates = ["spark"];

        Assert.Equal("Spark", CultureRules.TakeAName(colony, finder));
    }

    [Fact]
    public void ASentenceWhereTheWordShouldBeIsNotAWord()
    {
        var text = """{ "name": "the thing that we found in the hall that nobody understands", "rationale": "x" }""";

        Assert.Null(CoinageCall.Parse(new CompletionResult { Model = "test-deep", Text = text }).Term);
    }

    // ---- myth ---------------------------------------------------------------------------------

    [Fact]
    public void AStoryWaitsForSomethingToBeAbout()
    {
        var (colony, _) = Start();
        colony.Tick = 3 * Colony.TicksPerDay;

        Assert.False(CultureRules.MythDue(colony));

        colony.ArtifactsRecovered = CultureRules.FirstTellingEvidence;
        Assert.True(CultureRules.MythDue(colony));
    }

    [Fact]
    public void TheStoryIsBuiltFromWhatTheColonyHasSeen()
    {
        var (colony, body) = Start();
        colony.ArtifactsRecovered = 4;
        PlayerVerbs.Reveal(colony, body, colony.Colonists[0], "ledger");

        var prompt = MythCall.BuildPrompt(colony);

        Assert.Contains("four", prompt);
        Assert.Contains(Revelations.Find("ledger")!.Shown, prompt);
        Assert.Contains("This is the first time it has been told.", prompt);

        // No figures for the model to hand back as something the colony knows. Designations like
        // C-01 are names, not figures, and are left out of the check.
        Assert.DoesNotMatch(@"\b\d+\b", System.Text.RegularExpressions.Regex.Replace(prompt, @"C-\d+", "C-"));
    }

    /// <summary>
    /// The golden rule, for the myth: the earlier telling travels in the request. The server is
    /// never told there was one.
    /// </summary>
    [Fact]
    public async Task ARetellingCarriesTheLastTellingInItsOwnRequest()
    {
        var (colony, _) = Start();
        colony.ArtifactsRecovered = 3;
        using var mind = Answering(Told, out var handler);

        await mind.TellMythAsync(colony);
        colony.ArtifactsRecovered = 6;
        await mind.TellMythAsync(colony);

        var sent = handler.LastRequestBody!;
        Assert.Contains("As it was last told", sent);
        Assert.Contains("remember too much", sent);

        using var document = System.Text.Json.JsonDocument.Parse(sent);
        Assert.Equal(
            new HashSet<string> { "model", "system", "prompt", "stream", "keep_alive", "think", "format", "options" },
            document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet());

        Assert.Equal(2, colony.MythHistory.Count);
        Assert.Equal(2, colony.Myth!.Version);
    }

    [Fact]
    public async Task TheStoryReachesEveryMindAndMarksWhoGoesAgainstIt()
    {
        var (colony, _) = Start();
        colony.ArtifactsRecovered = 3;
        var doubter = colony.Colonists[2];
        doubter.Memory.Add(new Belief
        {
            Id = "b-doubt", OwnerId = doubter.Id, FormedOnDay = 1, Claim = "The makers never meant to come back.",
            SourceMemoryIds = [], Confidence = 0.7
        });

        using var mind = Answering(Told, out _);
        await mind.TellMythAsync(colony);

        Assert.StartsWith("The makers built the Loom", colony.Myth!.Story);
        Assert.Contains("remember too much", WorldPrompt.SystemFor(colony.Colonists[0], colony));

        // Right and outnumbered — or wrong and alone. An id nobody holds is ignored.
        Assert.True(doubter.Memory.Beliefs.Single().ContradictsColonyMyth);
        Assert.Equal(["b-doubt"], colony.Myth.ContradictedBeliefIds);
        Assert.Contains(colony.Feed.Recent(5), e => e.Layer == EventLayer.Mind && e.Text.StartsWith("The colony has begun to tell a story"));
    }

    [Fact]
    public async Task WithNoMindThereIsNoStory()
    {
        var (colony, _) = Start();
        colony.ArtifactsRecovered = 3;
        using var mind = new MindService(Unreachable());

        var result = await mind.TellMythAsync(colony);

        Assert.False(result.FromModel);
        Assert.Null(colony.Myth);
        Assert.Equal(string.Empty, WorldPrompt.ColonyCulture(colony));
    }
}
