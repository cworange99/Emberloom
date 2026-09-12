using Emberloom.Core.Colonists;
using Emberloom.Core.Events;
using Emberloom.Core.Forks;
using Emberloom.Core.Simulation;
using Emberloom.Mind;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Ollama;
using Xunit;

namespace Emberloom.Tests;

/// <summary>
/// M5 Deliberation: each council member's say, from their own mind. The vote is counted, heard,
/// and traceable to the call that wrote it; a late one changes nothing; a dead mind still votes,
/// marked as not having thought.
/// </summary>
public class DeliberationTests
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
            { "response": {{escaped}}, "prompt_eval_count": 400, "eval_count": 70,
              "total_duration": 4000000000, "load_duration": 0,
              "prompt_eval_duration": 1000000000, "eval_duration": 3000000000 }
            """;
    }

    private const string ForIt = """
        { "stance": "for", "reasoning": "We are too few to hold the city.", "line": "Print it. We need the hands.", "conviction": 0.8 }
        """;

    private static (Colony Colony, BodySimulation Body, Fork Fork) Council()
    {
        var random = new Random(11);
        var colony = ColonyFactory.Create(8, random);
        colony.Tick = (long)(ForkRules.EarliestDay * Colony.TicksPerDay);
        colony.LoomCharge = ForkRules.PrintThreshold + 1;

        var fork = ForkRules.Detect(colony)!;
        colony.OpenFork = fork;
        return (colony, new BodySimulation(colony, random), fork);
    }

    private static MindService Answering(string json) =>
        new(new OllamaClient(Options(), new HttpClient(new StubHandler(Envelope(json)))));

    [Fact]
    public void AStanceIsReadFromTheSchema()
    {
        var (colony, _, fork) = Council();
        var member = colony.Find(fork.Participants[0])!;

        var result = DeliberationCall.Parse(new CompletionResult { Model = "test-deep", Text = ForIt }, member, fork);

        Assert.True(result.FromModel);
        Assert.Equal(Stance.For, result.Vote.Stance);
        Assert.Equal("Print it. We need the hands.", result.Vote.Line);
        Assert.Equal(0.8, result.Vote.Conviction, precision: 3);
        Assert.False(result.Vote.WasFallback);
    }

    [Fact]
    public void AnAnswerWithNoCountableStanceFallsBackToWhereTheyStand()
    {
        var (colony, _, fork) = Council();
        var member = colony.Find(fork.Participants[0])!;
        var text = """{ "stance": "maybe", "reasoning": "", "line": "Hm.", "conviction": 0.5 }""";

        var result = DeliberationCall.Parse(new CompletionResult { Model = "test-deep", Text = text }, member, fork);

        Assert.False(result.FromModel);
        Assert.True(result.Vote.WasFallback);
    }

    /// <summary>
    /// Live at M5, a 3B model answered a 0-to-1 conviction with 6. Read it on the scale it was
    /// plainly given on, rather than clamping every such answer to certainty.
    /// </summary>
    [Theory]
    [InlineData(0.7, 0.7)]
    [InlineData(6, 0.6)]
    [InlineData(85, 0.85)]
    [InlineData(-1, 0)]
    [InlineData(4000, 1)]
    public void AConvictionOnTheWrongScaleIsReadOnTheScaleItWasGivenOn(double given, double read) =>
        Assert.Equal(read, DeliberationCall.Conviction(given), precision: 3);

    /// <summary>
    /// The golden rule, for Deliberation: the fork, the stakes and the colonist all travel in the
    /// request, whose shape is exactly Kindling's — nothing the server could remember a council by.
    /// </summary>
    [Fact]
    public async Task TheCallCarriesItsOwnContextAndAsksTheServerToRememberNothing()
    {
        var (colony, _, fork) = Council();
        var member = colony.Find(fork.Participants[0])!;
        var handler = new StubHandler(Envelope(ForIt));
        using var client = new OllamaClient(Options(), new HttpClient(handler));

        await DeliberationCall.ExecuteAsync(client, member, colony, fork);

        var sent = handler.LastRequestBody!;
        Assert.Contains("A machine called the Loom", sent);
        Assert.Contains(fork.Question, sent);

        using var document = System.Text.Json.JsonDocument.Parse(sent);
        var fields = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        Assert.Equal(
            new HashSet<string> { "model", "system", "prompt", "stream", "keep_alive", "think", "format", "options" },
            fields);
    }

    [Fact]
    public void LaterSpeakersHearWhatWasAlreadySaid()
    {
        var (colony, _, fork) = Council();
        var first = colony.Find(fork.Participants[0])!;
        fork.Cast(first.Id, new Vote(Stance.Against, 0.9, "Keep the charge for the dark.", "", false));

        var prompt = DeliberationCall.BuildPrompt(colony.Find(fork.Participants[1])!, colony, fork);

        Assert.Contains("Keep the charge for the dark.", prompt);
        Assert.Contains(first.DisplayName, prompt);

        // No raw figures for the model to hand back as a conviction.
        Assert.DoesNotMatch(@"\d+\.\d+", prompt);
    }

    [Fact]
    public async Task AVoteIsCountedHeardAndTraceable()
    {
        var (colony, _, fork) = Council();
        var member = colony.Find(fork.Participants[0])!;
        using var mind = Answering(ForIt);

        await mind.DeliberateAsync(member, colony, fork);

        var vote = fork.Votes[member.Id];
        Assert.Equal(Stance.For, vote.Stance);
        Assert.False(vote.WasFallback);

        // The vote names the call that wrote it, so the inspector can open it.
        var call = member.FindCall(vote.CallId!);
        Assert.Equal("Deliberation", call!.CallType);

        var said = colony.Feed.Recent(10).Last(e => e.Quote is not null);
        Assert.Equal("Print it. We need the hands.", said.Quote);
        Assert.True(said.IsNotable);
        Assert.Equal(EventLayer.Mind, said.Layer);
    }

    [Fact]
    public async Task AVoteThatArrivesAfterTheDecisionChangesNothing()
    {
        var (colony, body, fork) = Council();
        var late = colony.Find(fork.Participants[^1])!;

        foreach (var id in fork.Participants.Where(id => id != late.Id))
        {
            fork.Cast(id, new Vote(Stance.Against, 1, "no", "", false));
        }

        for (var tick = 0; tick < (ForkRules.TimeoutDays + 0.1) * Colony.TicksPerDay && fork.IsOpen; tick++)
        {
            body.Tick();
        }

        Assert.False(fork.IsOpen);
        var heardBefore = colony.Feed.Recent(2000).Count(e => e.Quote is not null);

        using var mind = Answering(ForIt);
        await mind.DeliberateAsync(late, colony, fork);

        // Counted by where they stood when the deadline came, and nothing said afterwards is heard.
        Assert.True(fork.Votes[late.Id].WasFallback);
        Assert.Equal(heardBefore, colony.Feed.Recent(2000).Count(e => e.Quote is not null));

        // The call still happened and is on record.
        Assert.Equal("Deliberation", late.Trace[^1].CallType);
    }

    [Fact]
    public async Task AnUnreachableMindVotesWhereTheColonistStandsAndSaysSo()
    {
        var (colony, _, fork) = Council();
        var member = colony.Find(fork.Participants[0])!;
        using var mind = new MindService(Unreachable());

        var result = await mind.DeliberateAsync(member, colony, fork);

        Assert.False(result.FromModel);
        Assert.True(fork.Votes[member.Id].WasFallback);
        Assert.Equal(EventLayer.World, colony.Feed.Recent(10).Last(e => e.Quote is not null).Layer);
    }
}
