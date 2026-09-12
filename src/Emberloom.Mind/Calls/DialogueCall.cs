using Emberloom.Core.Colonists;
using Emberloom.Mind.Ollama;
using Emberloom.Mind.Prompts;

namespace Emberloom.Mind.Calls;

public sealed record SpokenLine(string SpeakerId, string Text);

public sealed record DialogueResult
{
    public required IReadOnlyList<SpokenLine> Lines { get; init; }

    /// <summary>False when the mind was unavailable, or answered with nothing usable, and stock lines stood in.</summary>
    public required bool FromModel { get; init; }

    public CompletionResult? Call { get; init; }
}

/// <summary>
/// Dialogue — two colonists talking. The one call with no schema: spoken lines are prose, and a
/// schema would buy nothing but a stiffer voice. Truncation here only shortens a line rather than
/// corrupting a document, which is what makes going without one affordable.
/// <para>
/// One call writes both sides of a short exchange — what the first says and the other's reply —
/// rather than one call per line. Half the calls, and the reply can actually answer what was said.
/// </para>
/// </summary>
public static class DialogueCall
{
    public static async Task<DialogueResult> ExecuteAsync(
        OllamaClient client,
        Colonist speaker,
        Colonist listener,
        Colony colony,
        CancellationToken ct = default)
    {
        var request = new CompletionRequest
        {
            System = WorldPrompt.SystemFor(speaker, colony),
            Prompt = BuildPrompt(speaker, listener, colony),
            Schema = null,
            MaxTokens = CallBudgets.For(CallType.Dialogue),
            Temperature = 0.9,
            Lane = Lane.Deep
        };

        var result = await client.CompleteAsync(request, ct).ConfigureAwait(false);
        return Parse(result, speaker, listener);
    }

    /// <summary>
    /// The speaker's own personality is the system prompt, as for every call. The listener, how the
    /// two read each other, and what is on each of their minds go in here — the volatile part.
    /// </summary>
    public static string BuildPrompt(Colonist speaker, Colonist listener, Colony colony)
    {
        var place = colony.Map.Find(speaker.PlaceId)?.Name ?? "the city";
        var toward = WorldPrompt.Reading(colony.Relationships.Peek(speaker.Id, listener.Id));
        var back = WorldPrompt.Reading(colony.Relationships.Peek(listener.Id, speaker.Id));

        // Something to talk about: what they have been through together, what is on each of their
        // minds, and the thing the speaker holds most firmly.
        var shared = speaker.Memory
            .MostSalient(colony.Day, 60)
            .Where(m => m.Actors.Contains(listener.Id))
            .Take(2)
            .Select(m => $"- {m.Describe()}");

        var mine = speaker.Memory
            .MostSalient(colony.Day, 4)
            .Where(m => !m.Actors.Contains(listener.Id))
            .Take(2)
            .Select(m => $"- {m.Describe()}");

        var theirs = listener.Memory.MostSalient(colony.Day, 2).Select(m => $"- {m.Describe()}");

        var belief = speaker.Memory.Beliefs.OrderByDescending(b => b.Confidence).FirstOrDefault();
        var believes = belief is null ? string.Empty : $"\n- believes: {belief.Claim}";

        return $"""
            [Scene]
            {speaker.DisplayName} and {listener.DisplayName} are together at {place}, {(colony.IsNight ? "after dark" : "in daylight")}. {speaker.DisplayName} speaks first.

            [{listener.DisplayName}]
            {listener.Personality}
            {speaker.DisplayName} finds them {toward}. They find {speaker.DisplayName} {back}.

            [Between them]
            {Rows(shared)}

            [On {speaker.DisplayName}'s mind]
            {Rows(mine)}{believes}

            [On {listener.DisplayName}'s mind]
            {Rows(theirs)}

            [Task]
            Write what {speaker.DisplayName} says, then what {listener.DisplayName} says back. Two lines,
            one sentence each, each starting with the speaker's name and a colon:
            {speaker.DisplayName}: ...
            {listener.DisplayName}: ...
            Something one of them would actually say about their life here, in their own voice.
            No stage directions, no quotation marks.
            """;
    }

    public static DialogueResult Parse(CompletionResult result, Colonist speaker, Colonist listener)
    {
        var lines = new List<SpokenLine>();

        foreach (var raw in result.Text.Split('\n'))
        {
            var (who, text) = Attribute(raw, speaker, listener);
            if (who is null || text.Length < 2)
            {
                continue;
            }

            lines.Add(new SpokenLine(who.Id, text));
            if (lines.Count == 2)
            {
                break;
            }
        }

        // Small models sometimes skip the names and write one bare line. It is the speaker's: they
        // were the one asked to speak first.
        if (lines.Count == 0)
        {
            var bare = result.Text.Split('\n').Select(Clean).FirstOrDefault(t => t.Length >= 2);
            if (bare is not null)
            {
                lines.Add(new SpokenLine(speaker.Id, bare));
            }
        }

        // A reply cut off by the token cap stops mid-sentence. Keep what was finished rather than
        // put half a thought in someone's mouth.
        if (lines.Count > 0
            && result.CompletionTokens >= CallBudgets.For(CallType.Dialogue)
            && !EndsASentence(lines[^1].Text))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines.Count == 0
            ? Fallback(speaker, listener, result)
            : new DialogueResult { Lines = lines, FromModel = true, Call = result };
    }

    /// <summary>
    /// The templated fallback. Thin on purpose: a colony with no mind speaks in stock lines, and the
    /// trace records them as a fallback so they are never mistaken for two minds talking.
    /// </summary>
    public static DialogueResult Fallback(Colonist speaker, Colonist listener, CompletionResult? call = null)
    {
        string[][] exchanges =
        [
            ["Anything worth knowing today?", "Nothing that would keep."],
            ["Still at it?", "Somebody has to be."],
            ["Did you sleep?", "Some. Not enough."],
            ["The Loom sounded different this morning.", "It always sounds different to you."]
        ];

        var pick = exchanges[StableHash(speaker.Id + listener.Id) % exchanges.Length];

        return new DialogueResult
        {
            Lines = [new SpokenLine(speaker.Id, pick[0]), new SpokenLine(listener.Id, pick[1])],
            FromModel = false,
            Call = call
        };
    }

    private static (Colonist? Who, string Text) Attribute(string raw, Colonist speaker, Colonist listener)
    {
        var line = raw.Trim().Trim('*', ' ');
        var colon = line.IndexOf(':');

        // A name is short. A colon further in is punctuation inside a sentence, not a speaker.
        if (colon <= 0 || colon > 24)
        {
            return (null, string.Empty);
        }

        var name = line[..colon].Trim().Trim('*', ' ');
        var text = Clean(line[(colon + 1)..]);

        Colonist? who =
            IsCalled(speaker, name) ? speaker
            : IsCalled(listener, name) ? listener
            : null;

        return (who, text);
    }

    private static bool IsCalled(Colonist colonist, string name) =>
        string.Equals(colonist.DisplayName, name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(colonist.Designation, name, StringComparison.OrdinalIgnoreCase);

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
