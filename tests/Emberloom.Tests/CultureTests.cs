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
    public void KindlingsCandidatesAreASafetyNetNotThePath()
    {
        var (colony, _) = Start();
        var first = colony.Colonists[0];
        var second = colony.Colonists[1];

        // Everyone arrives named, so there is nothing for TakeAName to do. It survives for a
        // colonist built outside ColonyFactory — a test, or a save written before M9.
        Assert.Null(CultureRules.TakeAName(colony, first));

        first.Name = null;
        first.NameCandidates = ["Ash", "Reed"];
        second.Name = "Ash";

        Assert.Equal("Reed", CultureRules.TakeAName(colony, first));
        Assert.Null(CultureRules.TakeAName(colony, first));
    }

    [Fact]
    public void AMindNobodyWroteStillHasAName()
    {
        var (colony, _) = Start();
        var unwritten = colony.Colonists[0];

        // The M9 fix, and the golden rule in one line. Kindling's fallback returns no candidates
        // at all, so before this a colonist printed while Ollama was unreachable could never be
        // named — a colony with no mind was a colony of numbers.
        Assert.Empty(unwritten.NameCandidates);
        Assert.NotNull(unwritten.Name);
        Assert.NotEqual(unwritten.Designation, unwritten.DisplayName);
    }

    [Fact]
    public void EveryColonistIsNamedWhenItIsMade()
    {
        var (colony, _) = Start();
        var random = new Random(3);

        var printed = ColonyFactory.Print(colony, random, chargeIsFree: true);
        var born = ColonyFactory.Pair(colony, colony.Colonists[0], colony.Colonists[1], random);

        Assert.NotNull(printed!.Name);
        Assert.NotNull(born.Name);

        // Nobody shares a name. Over a long run the generator will collide eventually; Names.Unused
        // walks the hash rather than the random stream, so collisions cost a name, not determinism.
        var names = colony.Colonists.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TheSameSeedGrowsTheSamePeopleAndADifferentSeedDoesNot()
    {
        var first = ColonyFactory.Create(4, new Random(4), seed: 4).Colonists.Select(c => c.Name).ToList();
        var same = ColonyFactory.Create(4, new Random(4), seed: 4).Colonists.Select(c => c.Name).ToList();
        var other = ColonyFactory.Create(4, new Random(99), seed: 99).Colonists.Select(c => c.Name).ToList();

        Assert.Equal(first, same);

        // Without the colony's seed mixed in, every colony ever started shared one cast: C-01 was
        // Arima in all of them, because the name came from the designation alone.
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void NamesArePronounceableAndLookLikeNames()
    {
        var colony = ColonyFactory.Create(4, new Random(11), seed: 11);
        var random = new Random(11);

        for (var i = 0; i < 40; i++)
        {
            ColonyFactory.Print(colony, random, chargeIsFree: true);
        }

        foreach (var name in colony.Colonists.Select(c => c.Name!))
        {
            Assert.InRange(name.Length, 4, 12);
            Assert.Equal(char.ToUpperInvariant(name[0]), name[0]);
            Assert.DoesNotContain(name, n => char.IsDigit(n) && name.Length <= 8);

            // Three vowels or three consonants in a row is a sound nobody would say twice.
            var letters = name.TakeWhile(char.IsLetter).ToList();
            for (var i = 2; i < letters.Count; i++)
            {
                var vowels = letters.Skip(i - 2).Take(3).Count(c => "aeiouyAEIOUY".Contains(c));
                Assert.InRange(vowels, 1, 2);
            }
        }
    }

    [Fact]
    public void ADeedBecomesAnEpithetRatherThanAName()
    {
        var (colony, body) = Start();
        var namesBefore = colony.Colonists.ToDictionary(c => c.Id, c => c.DisplayName);

        // Which colonist is first to anything is the scorer's business, so this waits for the
        // colony rather than for one person.
        for (var tick = 0; tick < 40 * Colony.TicksPerDay && !colony.Colonists.Any(c => c.Epithet is not null); tick++)
        {
            body.Tick();
        }

        // The deeds are one-shot per place and run out; a name must not depend on them. What a
        // deed buys now is being known for something, which is what it was always worth.
        var known = colony.Colonists.Where(c => c.Epithet is not null).ToList();
        Assert.NotEmpty(known);
        Assert.All(known, c => Assert.Equal(namesBefore[c.Id], c.DisplayName));
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
        finder.Name = null;
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
    /// M18: a retelling is told what is new since the last one.
    /// <para>
    /// The prompt used to hand a mind the previous telling and the whole evidence list with no mark
    /// on either, while instructing it to keep most of how it was last told. There was nothing in
    /// it that could change the story, and across 158 tellings in the overnight run the story did
    /// not change — near word for word, every time. What is new is now marked as new, and the task
    /// asks for a story that accounts for it.
    /// </para>
    /// </summary>
    [Fact]
    public void ARetellingIsToldWhatHasHappenedSince()
    {
        var (colony, body) = Start();
        colony.ArtifactsRecovered = 2;

        colony.MythHistory.Add(new MythTelling
        {
            Version = 1,
            Story = "The makers wove the world and went away.",
            ToldOnDay = colony.Day,
            Evidence = CultureRules.Evidence(colony),
            ArtifactsThen = colony.ArtifactsRecovered,
            RevealedThen = colony.Revealed.Keys.ToList()
        });

        // Two things the colony did not have when it last told the story.
        colony.ArtifactsRecovered = 5;
        PlayerVerbs.Reveal(colony, body, colony.Colonists[0], "ledger");

        var prompt = MythCall.BuildPrompt(colony);

        Assert.Contains("[What has happened since it was last told]", prompt);
        Assert.Contains("The makers wove the world and went away.", prompt);

        // The new evidence is under the new heading, and the old is not.
        var since = prompt[prompt.IndexOf("[What has happened since it was last told]", StringComparison.Ordinal)..];
        Assert.Contains(Revelations.Find("ledger")!.Shown, since);
        Assert.Contains("three more pieces", since);

        // And the instruction now gives it a reason to change rather than to repeat.
        Assert.Contains("accounts for what has happened since", prompt);
        Assert.DoesNotContain("Keep most of how it was last told", prompt);
    }

    /// <summary>
    /// And nothing is new when nothing has happened — a colony that has found nothing since is not
    /// handed an empty heading to invent something under.
    /// </summary>
    [Fact]
    public void ARetellingWithNothingNewIsNotToldThereIs()
    {
        var (colony, _) = Start();
        colony.ArtifactsRecovered = 2;

        colony.MythHistory.Add(new MythTelling
        {
            Version = 1,
            Story = "The makers wove the world and went away.",
            ToldOnDay = colony.Day,
            Evidence = CultureRules.Evidence(colony),
            ArtifactsThen = colony.ArtifactsRecovered,
            RevealedThen = colony.Revealed.Keys.ToList()
        });

        var prompt = MythCall.BuildPrompt(colony);

        Assert.DoesNotContain("[What has happened since it was last told]", prompt);
        Assert.Contains("[What the colony has long had to go on]", prompt);
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

    /// <summary>
    /// The story is a file a person can rewrite. What the file says is what the colony tells, and
    /// what it writes out reads back as the same story.
    /// </summary>
    [Fact]
    public void TheStoryGoesOutToItsFileAndComesBack()
    {
        var (colony, _) = Start();

        // An untold story leaves nothing to read back — an empty file is no story, not an empty one.
        Assert.Equal(string.Empty, MythFile.ReadStory(MythFile.Compose(colony)));

        const string written = "The makers walked into the sea, and left the Loom counting the ones who stayed.";
        Assert.True(CultureRules.TellByHand(colony, written));

        var file = MythFile.Compose(colony);
        Assert.Contains(MythFile.Heading, file);
        Assert.Contains("by hand", file);
        Assert.Equal(written, MythFile.ReadStory(file));
    }

    [Fact]
    public void AStoryWrittenByHandIsTheStoryFromThenOn()
    {
        var (colony, _) = Start();
        const string written = "The makers walked into the sea, and left the Loom counting the ones who stayed.";

        Assert.True(CultureRules.TellByHand(colony, written));

        Assert.Equal(written, colony.Myth!.Story);
        Assert.True(colony.Myth.ByHand);
        Assert.Contains(written, WorldPrompt.SystemFor(colony.Colonists[0], colony));
        Assert.Contains(colony.Feed.Recent(5), e => e.Layer == EventLayer.Player && e.Text.Contains("told differently now"));

        // The same story again is not a new telling, and neither is a scrap.
        Assert.False(CultureRules.TellByHand(colony, $"  {written}  "));
        Assert.False(CultureRules.TellByHand(colony, "hm"));
        Assert.Single(colony.MythHistory);
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
