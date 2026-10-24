using Emberloom.Core.Colonists;
using Emberloom.Core.Events;
using Emberloom.Core.Forks;
using Emberloom.Core.Simulation;
using Emberloom.Core.Verbs;
using Emberloom.Mind;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Ollama;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M6 phase A — the voice. Speak is remembered whether or not a mind answers, and weighed heavily;
/// the answer is one self-contained call. Print spends the Loom's charge on a seed you write, and is
/// never allowed over the top of the colony's own vote on exactly that.
/// </summary>
public class PlayerVerbTests
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

    private static string Envelope(string text)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(text);
        return $$"""
            { "response": {{escaped}}, "prompt_eval_count": 400, "eval_count": 30,
              "total_duration": 2000000000, "load_duration": 0,
              "prompt_eval_duration": 1000000000, "eval_duration": 1000000000 }
            """;
    }

    private static (Colony Colony, BodySimulation Body, Random Random) Start()
    {
        var random = new Random(11);
        var colony = ColonyFactory.Create(8, random);
        return (colony, new BodySimulation(colony, random), random);
    }

    private static MindService Answering(string text) =>
        new(new OllamaClient(Options(), new HttpClient(new StubHandler(Envelope(text)))));

    // ---- Speak -----------------------------------------------------------------------------

    [Fact]
    public void WhatYouSayIsRememberedAndWeighedHeavily()
    {
        var (colony, body, _) = Start();
        var listener = colony.Colonists[2];

        var result = PlayerVerbs.Speak(colony, body, listener, "  You are not the first.  ");

        Assert.True(result.Ok);
        var heard = listener.Memory.FindEpisodic(result.Memory!.Id)!;
        Assert.Equal(PlayerVerbs.HeardVerb, heard.Verb);
        Assert.Contains("“You are not the first.”", heard.Describe());

        // First in line for the next reflection: that is what "can become a belief" rests on.
        Assert.Equal(heard.Id, listener.Memory.ReflectionBatch(colony.Day, 1)[0].Id);

        Assert.Contains(colony.Feed.Recent(5), e => e.Layer == EventLayer.Player && e.Text.Contains("You are not the first."));

        // Nobody else heard it.
        Assert.All(colony.Colonists.Where(c => !ReferenceEquals(c, listener)),
            c => Assert.DoesNotContain(c.Memory.Episodic, m => m.Verb == PlayerVerbs.HeardVerb));
    }

    [Fact]
    public void SilenceAndSpeechesAreRefused()
    {
        var (colony, body, _) = Start();
        var listener = colony.Colonists[0];

        Assert.False(PlayerVerbs.Speak(colony, body, listener, "   ").Ok);
        Assert.False(PlayerVerbs.Speak(colony, body, listener, new string('a', PlayerVerbs.MaxWords + 1)).Ok);
        Assert.DoesNotContain(listener.Memory.Episodic, m => m.Verb == PlayerVerbs.HeardVerb);
    }

    // ---- Print -----------------------------------------------------------------------------

    [Fact]
    public void PrintingSpendsTheChargeOnTheSeedYouWrote()
    {
        var (colony, body, random) = Start();
        colony.LoomCharge = 30;

        var result = PlayerVerbs.Print(colony, body, random, "keeps count of everything, and tells nobody the total");

        Assert.True(result.Ok);
        Assert.Equal(9, colony.Colonists.Count);
        Assert.Equal("keeps count of everything, and tells nobody the total", result.Colonist!.Seed);
        Assert.Equal(30 - Colony.PrintCost, colony.LoomCharge, precision: 6);

        // Everyone else felt the charge go.
        Assert.All(colony.Colonists.Where(c => !ReferenceEquals(c, result.Colonist)),
            c => Assert.Contains(c.Memory.Episodic, m => m.Verb == "felt the Loom spend its charge on"));
    }

    [Fact]
    public void PrintingWithNoSeedRollsOne()
    {
        var (colony, body, random) = Start();
        colony.LoomCharge = 30;

        var result = PlayerVerbs.Print(colony, body, random, "   ");

        Assert.True(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Colonist!.Seed));
    }

    [Fact]
    public void NoChargeNoPrint()
    {
        var (colony, body, random) = Start();
        colony.LoomCharge = Colony.PrintCost - 1;

        var result = PlayerVerbs.Print(colony, body, random, null);

        Assert.False(result.Ok);
        Assert.Equal(8, colony.Colonists.Count);
    }

    /// <summary>
    /// A player who could print over the top of an open vote would make every Print fork
    /// decorative. The colony finishes deciding first.
    /// </summary>
    [Fact]
    public void YouCannotPrintOverTheColonysOwnVote()
    {
        var (colony, body, random) = Start();
        colony.Tick = (long)(ForkRules.EarliestDay * Colony.TicksPerDay);
        colony.LoomCharge = ForkRules.ChargeNeededToPrint(colony) + 1;
        colony.OpenFork = ForkRules.Detect(colony);

        var result = PlayerVerbs.Print(colony, body, random, null);

        Assert.False(result.Ok);
        Assert.Contains("deciding", result.Refusal);
        Assert.Equal(8, colony.Colonists.Count);
    }

    // ---- the answer ------------------------------------------------------------------------

    /// <summary>
    /// The golden rule, for the player's words: they travel in the request with everything else,
    /// in the same shape as every other prose call, and nothing asks the server to remember them.
    /// </summary>
    [Fact]
    public async Task TheCallCarriesItsOwnContextAndAsksTheServerToRememberNothing()
    {
        var (colony, _, _) = Start();
        var listener = colony.Colonists[0];
        var handler = new StubHandler(Envelope("Who is there?"));
        using var client = new OllamaClient(Options(), new HttpClient(handler));

        await ReplyCall.ExecuteAsync(client, listener, colony, "The Loom is older than the city.");

        var sent = handler.LastRequestBody!;
        Assert.Contains("A machine called the Loom", sent);
        Assert.Contains("The Loom is older than the city.", sent);

        using var document = System.Text.Json.JsonDocument.Parse(sent);
        var fields = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        Assert.Subset(
            new HashSet<string> { "model", "system", "prompt", "stream", "keep_alive", "think", "format", "options" },
            fields);
        Assert.DoesNotContain("format", fields);
    }

    [Fact]
    public void TheVoiceIsRememberedFromOneTimeToTheNext()
    {
        var (colony, body, _) = Start();
        var listener = colony.Colonists[0];
        PlayerVerbs.Speak(colony, body, listener, "Count the doors.");

        var prompt = ReplyCall.BuildPrompt(listener, colony, "Did you count them?");

        Assert.Contains("It has spoken to them before", prompt);
        Assert.Contains("Count the doors.", prompt);
        Assert.Contains("Did you count them?", prompt);
    }

    [Fact]
    public async Task AnAnswerIsHeardAndKeptWithWhatWasSaid()
    {
        var (colony, body, _) = Start();
        var listener = colony.Colonists[0];
        var spoken = PlayerVerbs.Speak(colony, body, listener, "You are not the first.");
        using var mind = Answering($"{listener.DisplayName}: Then who was?");

        var result = await mind.AnswerAsync(listener, colony, "You are not the first.", spoken.Memory!.Id);

        Assert.True(result.FromModel);
        Assert.Equal("Then who was?", result.Line);
        Assert.Contains("Then who was?", listener.Memory.FindEpisodic(spoken.Memory.Id)!.Words);
        Assert.Equal("Dialogue", listener.Trace[^1].CallType);

        var said = colony.Feed.Recent(10).Last(e => e.Quote is not null);
        Assert.Equal("Then who was?", said.Quote);
        Assert.Equal(EventLayer.Mind, said.Layer);
    }

    [Fact]
    public async Task AStockAnswerIsNeverRememberedAsMeant()
    {
        var (colony, body, _) = Start();
        var listener = colony.Colonists[0];
        var spoken = PlayerVerbs.Speak(colony, body, listener, "You are not the first.");
        using var mind = new MindService(Unreachable());

        var result = await mind.AnswerAsync(listener, colony, "You are not the first.", spoken.Memory!.Id);

        Assert.False(result.FromModel);
        Assert.Null(listener.Memory.FindEpisodic(spoken.Memory.Id)!.Words);
        Assert.Equal(EventLayer.World, colony.Feed.Recent(10).Last(e => e.Quote is not null).Layer);
    }

    [Fact]
    public void AnAnswerCutOffMidSentenceKeepsWhatFinished()
    {
        var (colony, _, _) = Start();
        var listener = colony.Colonists[0];
        var cut = new CompletionResult
        {
            Model = "test-deep",
            Text = $"{listener.DisplayName}: I heard you. I am not sure wh",
            CompletionTokens = CallBudgets.For(CallType.Dialogue)
        };

        Assert.Equal("I heard you.", ReplyCall.Parse(cut, listener, "hello").Line);
    }
}
