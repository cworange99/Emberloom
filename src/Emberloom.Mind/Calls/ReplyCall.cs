using Emberloom.Core.Colonists;
using Emberloom.Core.Verbs;
using Emberloom.Mind.Ollama;
using Emberloom.Mind.Prompts;

namespace Emberloom.Mind.Calls;

public sealed record ReplyResult
{
    public required string Line { get; init; }

    /// <summary>False when the mind was unavailable, or answered with nothing usable, and a stock line stood in.</summary>
    public required bool FromModel { get; init; }

    public CompletionResult? Call { get; init; }
}

/// <summary>
/// Dialogue, when the player speaks (DESIGN §5: "also when the player speaks"): one colonist's
/// answer to the voice. Prose, no schema, on the Dialogue budget — the same reasons as
/// <see cref="DialogueCall"/>.
/// <para>
/// What was said travels in the request, along with anything the voice has said to them before
/// (read from their own memories, here). The inference server is never told it is talking to the
/// same colonist twice.
/// </para>
/// </summary>
public static class ReplyCall
{
    public static async Task<ReplyResult> ExecuteAsync(
        OllamaClient client,
        Colonist colonist,
        Colony colony,
        string words,
        CancellationToken ct = default)
    {
        var request = new CompletionRequest
        {
            System = WorldPrompt.SystemFor(colonist, colony),
            Prompt = BuildPrompt(colonist, colony, words),
            Schema = null,
            MaxTokens = CallBudgets.For(CallType.Dialogue),
            Temperature = 0.9,
            Lane = Lane.Deep
        };

        var result = await client.CompleteAsync(request, ct).ConfigureAwait(false);
        return Parse(result, colonist, words);
    }

    public static string BuildPrompt(Colonist colonist, Colony colony, string words)
    {
        var name = colonist.DisplayName;
        var place = colony.Map.Find(colonist.PlaceId)?.Name ?? "the city";
        var said = $"“{words}”";

        var before = colonist.Memory
            .MostSalient(colony.Day, 60)
            .Where(m => m.Verb == PlayerVerbs.HeardVerb && m.Object != said)
            .Take(3)
            .Select(m => $"- {m.Object}" + (m.Words is null ? string.Empty : $" ({m.Words})"))
            .ToList();

        var history = before.Count == 0
            ? "It has never spoken to them before."
            : "It has spoken to them before:\n" + string.Join("\n", before);

        var lately = colonist.Memory
            .MostSalient(colony.Day, 8)
            .Where(m => m.Verb != PlayerVerbs.HeardVerb)
            .Take(3)
            .Select(m => $"- {m.Describe()}");

        var belief = colonist.Memory.Beliefs.OrderByDescending(b => b.Confidence).FirstOrDefault();
        var believes = belief is null ? string.Empty : $"\n- believes: {belief.Claim}";

        return $"""
            [Scene]
            {name} is at {place}, {(colony.IsNight ? "after dark" : "in daylight")}. Nobody else can hear this.

            [The voice]
            A voice with no body, from nowhere {name} can see, has just said to them:
            {said}
            {history}

            [On {name}'s mind]
            {Rows(lately)}{believes}

            [Task]
            Write what {name} says back, aloud, in their own voice: one or two short sentences, under
            thirty words. They do not know what the voice is. Only mention places, people and things
            named here. No stage directions, no quotation marks, no name in front.
            """;
    }

    public static ReplyResult Parse(CompletionResult result, Colonist colonist, string words)
    {
        var lines = result.Text
            .Split('\n')
            .Select(l => StripName(l.Trim(), colonist))
            .Select(Clean)
            .Where(l => l.Length > 0)
            .ToList();

        var line = string.Join(" ", lines);

        // An answer cut off by the token cap stops mid-sentence. Keep the sentences that finished.
        if (result.CompletionTokens >= CallBudgets.For(CallType.Dialogue) && !EndsASentence(line))
        {
            var end = line.LastIndexOfAny(['.', '!', '?', '…']);
            line = end > 0 ? line[..(end + 1)] : string.Empty;
        }

        return line.Length < 2
            ? Fallback(colonist, words, result)
            : new ReplyResult { Line = line, FromModel = true, Call = result };
    }

    /// <summary>A stock answer, recorded as a fallback. Never remembered as something they meant.</summary>
    public static ReplyResult Fallback(Colonist colonist, string words, CompletionResult? call = null)
    {
        string[] stock =
        [
            "Who's there?",
            "I heard that. I don't know what to do with it.",
            "Say it again. Slower.",
            "…"
        ];

        return new ReplyResult
        {
            Line = stock[StableHash(colonist.Id + words) % stock.Length],
            FromModel = false,
            Call = call
        };
    }

    private static string StripName(string line, Colonist colonist)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0 || colon > 24)
        {
            return line;
        }

        var name = line[..colon].Trim().Trim('*', ' ');
        return string.Equals(name, colonist.DisplayName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, colonist.Designation, StringComparison.OrdinalIgnoreCase)
            ? line[(colon + 1)..]
            : line;
    }

    private static string Clean(string text) =>
        text.Trim().Trim('*', ' ', '"', '“', '”', '\'').Trim();

    private static bool EndsASentence(string text) =>
        text.Length > 0 && ".!?…\"”'".Contains(text[^1]);

    private static string Rows(IEnumerable<string> rows)
    {
        var list = rows.ToList();
        return list.Count == 0 ? "- nothing much" : string.Join("\n", list);
    }

    private static int StableHash(string text)
    {
        var h = 0;
        foreach (var ch in text)
        {
            h = (h * 31 + ch) & 0x7fffffff;
        }

        return h;
    }
}
