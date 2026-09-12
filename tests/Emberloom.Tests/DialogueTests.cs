using Emberloom.Core.Colonists;
using Emberloom.Core.Events;
using Emberloom.Core.Memory;
using Emberloom.Core.Simulation;
using Emberloom.Mind;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Ollama;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M5 Dialogue: two colonists talking, in one call. The words reach the feed, the speakers'
/// heads, and both of their memories — and a dead mind speaks in stock lines that say so.
/// </summary>
public class DialogueTests
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

    private static string Envelope(string text, int evalCount = 40)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(text);
        return $$"""
            { "response": {{escaped}}, "prompt_eval_count": 300, "eval_count": {{evalCount}},
              "total_duration": 3000000000, "load_duration": 0,
              "prompt_eval_duration": 1000000000, "eval_duration": 2000000000 }
            """;
    }

    private static (Colony Colony, Colonist Speaker, Colonist Listener) Pair()
    {
        var colony = ColonyFactory.Create(3, new Random(7));
        var speaker = colony.Colonists[0];
        var listener = colony.Colonists[1];
        speaker.PlaceId = "plaza";
        listener.PlaceId = "plaza";
        return (colony, speaker, listener);
    }

    private static CompletionResult Completion(string text, int completionTokens = 30) =>
        new() { Model = "test-deep", Text = text, CompletionTokens = completionTokens };

    private static void RememberTalking(Colony colony, Colonist colonist, Colonist other) =>
        colonist.Memory.Record(new EpisodicMemory
        {
            Id = $"m-{colonist.Id}-talk",
            Tick = colony.Tick,
            Day = colony.Day,
            Actors = [colonist.Id, other.Id],
            Verb = "talked with",
            Object = other.DisplayName,
            PlaceId = "plaza",
            Valence = 0.3,
            Intensity = 0.25
        });

    [Fact]
    public void BothLinesGoToTheirSpeakers()
    {
        var (_, speaker, listener) = Pair();
        var text = $"{speaker.DisplayName}: The stairwell again?\n{listener.DisplayName}: Every day until it opens.";

        var result = DialogueCall.Parse(Completion(text), speaker, listener);

        Assert.True(result.FromModel);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(speaker.Id, result.Lines[0].SpeakerId);
        Assert.Equal("The stairwell again?", result.Lines[0].Text);
        Assert.Equal(listener.Id, result.Lines[1].SpeakerId);
        Assert.Equal("Every day until it opens.", result.Lines[1].Text);
    }

    [Fact]
    public void ALineCutOffByTheCapIsDroppedRatherThanShownHalfSaid()
    {
        var (_, speaker, listener) = Pair();
        var text = $"{speaker.DisplayName}: Did you see it go dark?\n{listener.DisplayName}: I saw the light go out and then the";

        var result = DialogueCall.Parse(Completion(text, CallBudgets.For(CallType.Dialogue)), speaker, listener);

        var line = Assert.Single(result.Lines);
        Assert.Equal(speaker.Id, line.SpeakerId);
    }

    [Fact]
    public void ABareLineWithNoNameIsTheSpeakers()
    {
        var (_, speaker, listener) = Pair();

        var result = DialogueCall.Parse(Completion("\"Nobody sleeps well near that door.\""), speaker, listener);

        var line = Assert.Single(result.Lines);
        Assert.Equal(speaker.Id, line.SpeakerId);
        Assert.Equal("Nobody sleeps well near that door.", line.Text);
    }

    /// <summary>
    /// The golden rule, for the one call with no schema. The whole scene travels in the request —
    /// both colonists, how they read each other — and the request's shape has no handle the server
    /// could remember a conversation against. No format field either: dialogue is prose.
    /// </summary>
    [Fact]
    public async Task TheCallCarriesItsOwnContextAndAsksTheServerToRememberNothing()
    {
        var (colony, speaker, listener) = Pair();
        var handler = new StubHandler(Envelope($"{speaker.DisplayName}: Well?\n{listener.DisplayName}: Well."));
        using var client = new OllamaClient(Options(), new HttpClient(handler));

        await DialogueCall.ExecuteAsync(client, speaker, listener, colony);

        var sent = handler.LastRequestBody!;
        Assert.Contains("A machine called the Loom", sent);
        Assert.Contains(speaker.DisplayName, sent);
        Assert.Contains(listener.DisplayName, sent);

        using var document = System.Text.Json.JsonDocument.Parse(sent);
        var fields = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        Assert.Subset(
            new HashSet<string> { "model", "system", "prompt", "stream", "keep_alive", "think", "format", "options" },
            fields);
        Assert.DoesNotContain("format", fields);
    }

    [Fact]
    public async Task AnExchangeIsHeardInTheFeedAndRememberedByBoth()
    {
        var (colony, speaker, listener) = Pair();
        RememberTalking(colony, speaker, listener);
        RememberTalking(colony, listener, speaker);

        var reply = $"{speaker.DisplayName}: We should go up.\n{listener.DisplayName}: Not in the dark.";
        using var mind = new MindService(new OllamaClient(Options(), new HttpClient(new StubHandler(Envelope(reply)))));

        await mind.DialogueAsync(speaker, listener, colony);

        // Both minds were in it, so both traces have it.
        Assert.Equal("Dialogue", Assert.Single(speaker.Trace).CallType);
        Assert.Equal("Dialogue", Assert.Single(listener.Trace).CallType);

        // Each line is its own quote in the feed, from the mind, attributed to whoever said it.
        var spoken = colony.Feed.Recent(10).Where(e => e.Quote is not null).ToList();
        Assert.Equal(2, spoken.Count);
        Assert.All(spoken, e => Assert.Equal(EventLayer.Mind, e.Layer));
        Assert.Equal(speaker.Id, spoken[0].ColonistId);
        Assert.Equal("We should go up.", spoken[0].Quote);

        // And both of them remember what was said, on the conversation it was said in.
        Assert.Contains("We should go up.", speaker.Memory.Episodic.Single().Describe());
        Assert.Contains("Not in the dark.", listener.Memory.Episodic.Single().Describe());
    }

    [Fact]
    public async Task WordsThatArriveBeforeTheConversationIsRememberedWaitForIt()
    {
        var (colony, speaker, listener) = Pair();
        var reply = $"{speaker.DisplayName}: You first.\n{listener.DisplayName}: After you.";
        using var mind = new MindService(new OllamaClient(Options(), new HttpClient(new StubHandler(Envelope(reply)))));

        await mind.DialogueAsync(speaker, listener, colony);

        // No conversation memory yet — the mind answered before the body finished talking.
        Assert.Contains("You first.", speaker.PendingWords[listener.Id]);

        // The moment it is written, the words go onto it.
        RememberTalking(colony, speaker, listener);
        Assert.True(speaker.Memory.AttachWords(listener.Id, speaker.PendingWords[listener.Id], colony.Day));
        Assert.Contains("You first.", speaker.Memory.Episodic.Single().Describe());
    }

    [Fact]
    public async Task AnUnreachableMindSpeaksInStockLinesAndSaysSo()
    {
        var (colony, speaker, listener) = Pair();
        RememberTalking(colony, speaker, listener);
        using var mind = new MindService(Unreachable());

        var result = await mind.DialogueAsync(speaker, listener, colony);

        Assert.False(result.FromModel);
        Assert.Equal(2, result.Lines.Count);
        Assert.True(Assert.Single(speaker.Trace).WasFallback);

        // Stock lines go out as the world's, not the mind's, and nobody remembers them as meant.
        Assert.All(colony.Feed.Recent(10).Where(e => e.Quote is not null), e => Assert.Equal(EventLayer.World, e.Layer));
        Assert.Null(speaker.Memory.Episodic.Single().Words);
    }

    [Fact]
    public void TheBodyAnnouncesAConversationForTheMindToVoice()
    {
        var random = new Random(4);
        var colony = ColonyFactory.Create(8, random);
        var body = new BodySimulation(colony, random);

        (Colonist Speaker, Colonist Listener, string Place)? heard = null;
        body.ConversationStarted += (speaker, listener, place) => heard ??= (speaker, listener, place.Id);

        for (var tick = 0; tick < Colony.TicksPerDay * 10 && heard is null; tick++)
        {
            body.Tick();
        }

        Assert.NotNull(heard);
        Assert.NotEqual(heard!.Value.Speaker.Id, heard.Value.Listener.Id);
        Assert.Equal(heard.Value.Place, heard.Value.Listener.PlaceId);
    }
}
