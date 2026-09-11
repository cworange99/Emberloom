using System.Text;
using System.Text.Json;
using Emberloom.Core.Colonists;
using Emberloom.Core.Drives;
using Emberloom.Core.Memory;
using Emberloom.Mind.Ollama;
using Emberloom.Mind.Prompts;

namespace Emberloom.Mind.Calls;

public sealed record BeliefDraft(string Claim, IReadOnlyList<string> SourceMemoryIds, double Confidence);

public sealed record ReflectionResult
{
    public required string ConsolidatedText { get; init; }
    public required double Valence { get; init; }
    public required IReadOnlyList<string> SourceMemoryIds { get; init; }
    public required IReadOnlyList<BeliefDraft> Beliefs { get; init; }
    public string? RelationshipAbout { get; init; }
    public string? RelationshipReading { get; init; }
    public required IReadOnlyDictionary<Drive, double> WeightDrift { get; init; }
    public required bool FromModel { get; init; }
    public CompletionResult? Call { get; init; }
}

/// <summary>
/// Reflection — reads a batch of episodic rows and decides what they <em>meant</em>.
/// <para>
/// This is the core loop. Its output is the only thing in the design that turns a colony's history
/// into its culture: a consolidated memory standing in for the batch, and beliefs that each carry
/// the ids of the memories that produced them. Those links are what make a belief traceable back to
/// the tick where the real thing happened, and nothing in that chain is authored.
/// </para>
/// </summary>
public static class ReflectionCall
{
    public static async Task<ReflectionResult> ExecuteAsync(
        OllamaClient client,
        Colonist colonist,
        Colony colony,
        IReadOnlyList<EpisodicMemory> batch,
        CancellationToken ct = default)
    {
        if (batch.Count == 0)
        {
            return Fallback(batch);
        }

        var request = new CompletionRequest
        {
            System = WorldPrompt.SystemFor(colonist, colony),
            Prompt = BuildPrompt(colonist, batch),
            Schema = MindSchemas.Reflection(),
            MaxTokens = CallBudgets.For(CallType.Reflection),
            Temperature = 0.8,
            Lane = Lane.Deep
        };

        var result = await client.CompleteAsync(request, ct).ConfigureAwait(false);
        return Parse(result, batch);
    }

    public static string BuildPrompt(Colonist colonist, IReadOnlyList<EpisodicMemory> batch)
    {
        var rows = new StringBuilder();

        // No raw numbers. The model reads everything in a prompt as something the colonist knows,
        // and hands a figure straight back as a belief — measured at M4: "Teachers who have a
        // valence of +0.35 with me are likely to be effective and caring." So how a thing felt goes
        // in as words, and the rows are listed in the order they happened rather than dated.
        static string Felt(double valence) => valence switch
        {
            >= 0.6 => "it went very well",
            >= 0.2 => "it went well",
            > -0.2 => "nothing much either way",
            > -0.6 => "it went badly",
            _ => "it went very badly"
        };

        foreach (var memory in batch.OrderBy(m => m.Tick))
        {
            rows.AppendLine($"{memory.Id} {colonist.DisplayName} {memory.Describe()} — {Felt(memory.Valence)}");
        }

        return $"""
            [Recent memories]
            {rows.ToString().TrimEnd()}

            [Task]
            Decide what these meant to {colonist.DisplayName}.

            Write one consolidated memory that stands in for the whole batch — what a person would
            actually retain from those days, in their own terms, not a list of what happened.

            Then zero to two beliefs. A belief is a durable claim about the world, in this
            colonist's voice, that these memories would justify holding. Only write one if the
            memories genuinely support it — an ordinary week produces none, and a belief nobody
            earned is worse than no belief. Every belief must cite the memory ids it came from,
            using the ids exactly as they appear above.

            If the batch was about another colonist, say who and how this colonist reads them now.
            If it was not about anyone, leave those fields empty.

            Finally, any drift in the drive weights. Most reflections drift nothing: use 0 unless
            something in these memories would genuinely change what this mind reaches for, and keep
            any change small — this is a nudge, not a rewrite.
            """;
    }

    private static ReflectionResult Parse(CompletionResult result, IReadOnlyList<EpisodicMemory> batch)
    {
        try
        {
            using var document = JsonDocument.Parse(result.Text);
            var root = document.RootElement;

            if (!root.TryGetProperty("consolidated", out var consolidated))
            {
                return Fallback(batch, result);
            }

            var text = consolidated.TryGetProperty("text", out var textElement)
                ? textElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                return Fallback(batch, result);
            }

            var valence = consolidated.TryGetProperty("valence", out var valenceElement)
                          && valenceElement.TryGetDouble(out var v)
                ? v
                : 0;

            var validIds = batch.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var sources = ReadIds(consolidated, "source_memory_ids", validIds);
            if (sources.Count == 0)
            {
                // A consolidated memory that cites nothing cannot be traced, which defeats the
                // point. Fall back to citing the whole batch rather than losing the chain.
                sources = batch.Select(m => m.Id).ToList();
            }

            var beliefs = new List<BeliefDraft>();

            if (root.TryGetProperty("beliefs", out var beliefsElement)
                && beliefsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in beliefsElement.EnumerateArray())
                {
                    var claim = element.TryGetProperty("claim", out var claimElement)
                        ? claimElement.GetString()?.Trim() ?? string.Empty
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(claim))
                    {
                        continue;
                    }

                    // Only ids that actually exist in this batch. A model that invents a citation
                    // produces a belief whose chain dead-ends, and an untraceable belief is
                    // exactly the thing this design promises never to show.
                    var ids = ReadIds(element, "source_memory_ids", validIds);
                    if (ids.Count == 0)
                    {
                        continue;
                    }

                    var confidence = element.TryGetProperty("confidence", out var confidenceElement)
                                     && confidenceElement.TryGetDouble(out var c)
                        ? Math.Clamp(c, 0, 1)
                        : 0.5;

                    beliefs.Add(new BeliefDraft(claim, ids, confidence));
                }
            }

            string? about = null;
            string? reading = null;

            if (root.TryGetProperty("relationship_note", out var note))
            {
                about = note.TryGetProperty("about", out var aboutElement)
                    ? aboutElement.GetString()?.Trim()
                    : null;
                reading = note.TryGetProperty("reading", out var readingElement)
                    ? readingElement.GetString()?.Trim()
                    : null;
            }

            var drift = new Dictionary<Drive, double>();

            if (root.TryGetProperty("weight_drift", out var driftElement))
            {
                foreach (var drive in DriveTable.All)
                {
                    if (driftElement.TryGetProperty(DriveTable.Describe(drive), out var value)
                        && value.TryGetDouble(out var amount)
                        && Math.Abs(amount) > 0.001)
                    {
                        drift[drive] = Math.Clamp(amount, -0.2, 0.2);
                    }
                }
            }

            return new ReflectionResult
            {
                ConsolidatedText = text,
                Valence = Math.Clamp(valence, -1, 1),
                SourceMemoryIds = sources,
                Beliefs = beliefs.Take(2).ToList(),
                RelationshipAbout = string.IsNullOrWhiteSpace(about) ? null : about,
                RelationshipReading = string.IsNullOrWhiteSpace(reading) ? null : reading,
                WeightDrift = drift,
                FromModel = true,
                Call = result
            };
        }
        catch (JsonException)
        {
            return Fallback(batch, result);
        }
    }

    private static List<string> ReadIds(JsonElement parent, string property, HashSet<string> valid)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return element.EnumerateArray()
            .Select(e => e.GetString()?.Trim())
            .Where(id => id is not null && valid.Contains(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The templated fallback. Deliberately thin: it consolidates the batch by stating plainly
    /// that it happened, and forms no beliefs at all. A colony with no mind should visibly have no
    /// convictions, rather than convictions nobody wrote.
    /// </summary>
    public static ReflectionResult Fallback(
        IReadOnlyList<EpisodicMemory> batch,
        CompletionResult? call = null)
    {
        var summary = batch.Count == 0
            ? "Nothing worth keeping."
            : $"{batch.Count} things happened, and none of them were thought about.";

        return new ReflectionResult
        {
            ConsolidatedText = summary,
            Valence = batch.Count == 0 ? 0 : batch.Average(m => m.Valence),
            SourceMemoryIds = batch.Select(m => m.Id).ToList(),
            Beliefs = [],
            WeightDrift = new Dictionary<Drive, double>(),
            FromModel = false,
            Call = call
        };
    }
}
