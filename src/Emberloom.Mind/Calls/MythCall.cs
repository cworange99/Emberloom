using System.Text.Json;
using Emberloom.Core.Colonists;
using Emberloom.Core.Culture;
using Emberloom.Core.Life;
using Emberloom.Core.Verbs;
using Emberloom.Mind.Ollama;
using Emberloom.Mind.Prompts;

namespace Emberloom.Mind.Calls;

public sealed record MythResult
{
    /// <summary>The story, or null when nothing usable came back and the old telling stands.</summary>
    public string? Story { get; init; }

    public IReadOnlyList<string> ContradictedBy { get; init; } = [];

    public required bool FromModel { get; init; }

    public CompletionResult? Call { get; init; }
}

/// <summary>
/// Myth — the colony's founding story, about its own makers (DESIGN §1, §7). It does not have to
/// be true; nobody knows the truth. It has to be built from what the colony has actually seen,
/// found, read and decided, and it drifts a little each time it is retold.
/// <para>
/// Colony-level, so its system prompt is the world alone rather than any one colonist. Everything
/// it draws on travels in the request: the server is never told there was an earlier telling —
/// the earlier telling is simply in the prompt.
/// </para>
/// </summary>
public static class MythCall
{
    public static async Task<MythResult> ExecuteAsync(OllamaClient client, string prompt, CancellationToken ct = default)
    {
        var request = new CompletionRequest
        {
            System = WorldPrompt.WorldRules,
            Prompt = prompt,
            Schema = MindSchemas.Myth(),
            MaxTokens = CallBudgets.For(CallType.Myth),
            Temperature = 0.9,
            Lane = Lane.Deep
        };

        var result = await client.CompleteAsync(request, ct).ConfigureAwait(false);
        return Parse(result);
    }

    /// <summary>What the colony has to go on, what some of it believes, and how it was last told. No figures.</summary>
    public static string BuildPrompt(Colony colony)
    {
        var evidence = new List<string>
        {
            $"- Pieces of the makers' found in the ruins: {Count(colony.ArtifactsRecovered)}."
        };

        foreach (var (id, shownTo) in colony.Revealed)
        {
            if (Revelations.Find(id) is { } revelation)
            {
                evidence.Add($"- Shown to {colony.Find(shownTo)?.DisplayName ?? shownTo}: {revelation.Shown}");
            }
        }

        foreach (var core in colony.Cores.Where(c => c.Fate == CoreFate.Read))
        {
            var dead = colony.Find(core.ColonistId)?.DisplayName ?? core.ColonistId;
            foreach (var line in core.Heard.Take(2))
            {
                evidence.Add($"- Read from {dead}'s core after they died: “{line}”");
            }
        }

        foreach (var fork in colony.ForkHistory.TakeLast(5))
        {
            evidence.Add($"- The colony asked itself “{fork.Question}” and decided: {fork.Outcome}.");
        }

        var beliefs = colony.Living
            .SelectMany(c => c.Memory.Beliefs.Select(b => (Holder: c, Belief: b)))
            .OrderByDescending(x => AboutTheMakers(x.Belief.Claim))
            .ThenByDescending(x => x.Belief.Confidence)
            .Take(8)
            .Select(x => $"- {x.Belief.Id} ({x.Holder.DisplayName}): {x.Belief.Claim}")
            .ToList();

        var before = colony.Myth is { } myth
            ? $"""

              [As it was last told]
              {myth.Story}
              """
            : string.Empty;

        return $"""
            [What the colony has to go on]
            {string.Join("\n", evidence)}

            [What some of them believe]
            {(beliefs.Count == 0 ? "- nobody has come to believe anything yet" : string.Join("\n", beliefs))}
            {before}

            [Task]
            Tell the story this colony tells itself about the makers — who they were, why they left,
            what the Loom is for — in three or four sentences, the way it would be told aloud. It does
            not have to be true; nobody knows the truth. Build it from what the colony has to go on
            above. {(colony.Myth is null
                ? "This is the first time it has been told."
                : "Keep most of how it was last told: a retelling drifts, it does not start over.")}
            Then list the ids of any beliefs above that go against the story.
            """;
    }

    public static MythResult Parse(CompletionResult result)
    {
        try
        {
            using var document = JsonDocument.Parse(result.Text);
            var root = document.RootElement;

            var story = root.TryGetProperty("story", out var s) ? s.GetString()?.Trim() : null;

            var contradicted = root.TryGetProperty("contradicted_by", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(x => x.GetString()).OfType<string>().Select(x => x.Trim()).ToList()
                : [];

            if (string.IsNullOrWhiteSpace(story) || story.Length < 20)
            {
                return Fallback(result);
            }

            return new MythResult { Story = story, ContradictedBy = contradicted, FromModel = true, Call = result };
        }
        catch (JsonException)
        {
            return Fallback(result);
        }
    }

    /// <summary>No new telling: the story stays as it was. A mind is needed to tell one at all.</summary>
    public static MythResult Fallback(CompletionResult? call = null) => new() { FromModel = false, Call = call };

    private static int AboutTheMakers(string claim)
    {
        string[] words = ["maker", "loom", "city", "left", "built", "print", "gone"];
        return words.Any(w => claim.Contains(w, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }

    /// <summary>Counts as words: a figure in a prompt comes back as something the colony "knows".</summary>
    private static string Count(int n)
    {
        string[] words =
        [
            "none", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
            "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
            "nineteen", "twenty"
        ];

        return n >= 0 && n < words.Length ? words[n] : "a great many";
    }
}
