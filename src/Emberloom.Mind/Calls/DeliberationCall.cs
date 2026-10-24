using System.Text.Json;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Forks;
using Emberloom.Mind.Ollama;
using Emberloom.Mind.Prompts;

namespace Emberloom.Mind.Calls;

public sealed record DeliberationResult
{
    public required Vote Vote { get; init; }

    /// <summary>False when the mind was unavailable, or answered with no usable stance, and the fallback stood in.</summary>
    public required bool FromModel { get; init; }

    public CompletionResult? Call { get; init; }
}

/// <summary>
/// Deliberation — one council member's say at a genuine fork: a stance, a reason, the line they say
/// aloud, and how firmly they hold it. Undroppable: a colony that fast-forwards past its own turning
/// points is broken.
/// <para>
/// Each member hears what the members before them said. That is what makes a council an argument
/// rather than a poll — and why the order they are asked in matters.
/// </para>
/// </summary>
public static class DeliberationCall
{
    public static async Task<DeliberationResult> ExecuteAsync(
        OllamaClient client,
        Colonist colonist,
        Colony colony,
        Fork fork,
        CancellationToken ct = default)
    {
        var request = new CompletionRequest
        {
            System = WorldPrompt.SystemFor(colonist, colony),
            Prompt = BuildPrompt(colonist, colony, fork),
            Schema = MindSchemas.Deliberation(),
            MaxTokens = CallBudgets.For(CallType.Deliberation),
            Temperature = 0.8,
            Lane = Lane.Deep
        };

        var result = await client.CompleteAsync(request, ct).ConfigureAwait(false);
        return Parse(result, colonist, fork);
    }

    /// <summary>
    /// The question and what hangs on it, what has been said so far, and what this colonist believes
    /// and has lately lived through — their reasons have to come from their own history, or every
    /// council member argues the same generic case. No raw figures anywhere in it.
    /// </summary>
    public static string BuildPrompt(Colonist colonist, Colony colony, Fork fork)
    {
        var said = fork.Spoken
            .Select(s => $"- {colony.Find(s.ColonistId)?.DisplayName ?? s.ColonistId} ({Word(s.Vote.Stance)}): {s.Vote.Line}")
            .ToList();

        var beliefs = colonist.Memory.Beliefs
            .OrderByDescending(b => b.Confidence)
            .Take(3)
            .Select(b => $"- {b.Claim}");

        var lately = colonist.Memory.MostSalient(colony.Day, 3).Select(m => $"- {m.Describe()}");

        var already = said.Count == 0
            ? $"- nobody yet; {colonist.DisplayName} speaks first"
            : string.Join("\n", said);

        // The two drives the fork sets against each other, and how hard each pulls on this colonist.
        // The personality in the system prompt lists only what is unusual; this names the two that
        // matter here, so the stance comes from who they are rather than from the stakes' wording.
        var (forDrive, againstDrive) = ForkRules.Pulls(fork.Kind);

        return $"""
            [Fork]
            {fork.Question}
            {fork.Stakes}
            If the colony agrees: {fork.Proposal}

            [What pulls at {colonist.DisplayName} here]
            - {DriveTable.Describe(forDrive)}, which argues for: {WorldPrompt.PullOn(colonist.Weights, forDrive)}
            - {DriveTable.Describe(againstDrive)}, which argues against: {WorldPrompt.PullOn(colonist.Weights, againstDrive)}

            [Already said]
            {already}

            [What {colonist.DisplayName} believes]
            {Rows(beliefs)}

            [Lately]
            {Rows(lately)}

            [Task]
            Where does {colonist.DisplayName} stand? "for" means {fork.ForMeans}; "against" means
            {fork.AgainstMeans}; "abstain" means {fork.AbstainMeans}. Most people here have a view. Give their reasoning in one sentence, grounded in what they
            believe and what has happened to them; the one line they say aloud to the others, in their
            own words and not repeating anyone above, saying what they want the colony to do; and
            their conviction, from 0 (barely cares) to 1 (would stake everything on it).
            """;
    }

    public static DeliberationResult Parse(CompletionResult result, Colonist colonist, Fork fork)
    {
        try
        {
            using var document = JsonDocument.Parse(result.Text);
            var root = document.RootElement;

            Stance? stance = (root.TryGetProperty("stance", out var s) ? s.GetString() : null)?.Trim().ToLowerInvariant() switch
            {
                "for" => Stance.For,
                "against" => Stance.Against,
                "abstain" => Stance.Abstain,
                _ => null
            };

            var line = root.TryGetProperty("line", out var l)
                ? l.GetString()?.Trim().Trim('"', '“', '”').Trim()
                : null;

            var reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString()?.Trim() ?? string.Empty : string.Empty;

            var conviction = root.TryGetProperty("conviction", out var c) && c.TryGetDouble(out var value)
                ? Conviction(value)
                : 0.5;

            // A stance the colony cannot count, or a member with nothing to say aloud, is no say at
            // all: fall back to where they stand rather than invent one.
            if (stance is null || string.IsNullOrWhiteSpace(line))
            {
                return Fallback(colonist, fork, result);
            }

            return new DeliberationResult
            {
                Vote = new Vote(stance.Value, conviction, line, reasoning, WasFallback: false),
                FromModel = true,
                Call = result
            };
        }
        catch (JsonException)
        {
            return Fallback(colonist, fork, result);
        }
    }

    /// <summary>Where the colonist stands by their weights, with a stock line — and marked as a fallback.</summary>
    public static DeliberationResult Fallback(Colonist colonist, Fork fork, CompletionResult? call = null) => new()
    {
        Vote = ForkRules.FallbackVote(fork.Kind, colonist, fork.Pressure),
        FromModel = false,
        Call = call
    };

    /// <summary>
    /// Ollama's format constrains a number's type, not its range, and live at M5 a 3B model
    /// answered a 0-to-1 conviction with 6: it had picked a ten-point scale. Read out-of-range
    /// answers on the scale they were plainly given on rather than clamping every one to certainty.
    /// </summary>
    public static double Conviction(double value) => value switch
    {
        <= 1 => Math.Max(0, value),
        <= 10 => value / 10,
        <= 100 => value / 100,
        _ => 1
    };

    public static string Word(Stance stance) => stance switch
    {
        Stance.For => "for",
        Stance.Against => "against",
        _ => "abstains"
    };

    private static string Rows(IEnumerable<string> rows)
    {
        var list = rows.ToList();
        return list.Count == 0 ? "- nothing much" : string.Join("\n", list);
    }
}
