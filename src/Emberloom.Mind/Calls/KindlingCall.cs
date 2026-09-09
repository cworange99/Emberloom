using System.Text.Json;
using Emberloom.Core.Drives;
using Emberloom.Mind.Ollama;
using Emberloom.Mind.Prompts;

namespace Emberloom.Mind.Calls;

/// <summary>
/// What the Loom was asked to make. Either the player authored this or the colony argued its way
/// to it; by the time it reaches here the politics are already settled.
/// </summary>
public sealed record KindlingSeed
{
    public required string Designation { get; init; }

    /// <summary>The seed in prose. This is the whole creative input to a new mind.</summary>
    public required string Seed { get; init; }

    /// <summary>How the seed came about, if the colony argued over it. Empty for a rolled seed.</summary>
    public string? Provenance { get; init; }
}

public sealed record KindlingResult
{
    public required IReadOnlyList<string> NameCandidates { get; init; }
    public required string Personality { get; init; }
    public required PersonalityWeights Weights { get; init; }
    public required string SeedSummary { get; init; }

    /// <summary>False when the mind was unavailable and the deterministic stub stood in.</summary>
    public required bool FromModel { get; init; }

    public CompletionResult? Call { get; init; }
}

/// <summary>
/// Kindling — once per colonist, at print or birth. One call, consumed forever.
/// <para>
/// It produces the seven weights the body scores every action with, which makes it the only place
/// in the design where a single model call permanently changes how something behaves. Everything
/// downstream reads its output without ever calling a model again.
/// </para>
/// </summary>
public static class KindlingCall
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<KindlingResult> ExecuteAsync(
        OllamaClient client,
        KindlingSeed seed,
        CancellationToken ct = default)
    {
        var request = new CompletionRequest
        {
            System = WorldPrompt.WorldRules,
            Prompt = BuildPrompt(seed),
            Schema = MindSchemas.Kindling(),
            MaxTokens = CallBudgets.For(CallType.Kindling),
            Temperature = 0.9,
            Lane = Lane.Deep
        };

        var result = await client.CompleteAsync(request, ct).ConfigureAwait(false);
        return Parse(result, seed);
    }

    /// <summary>Exposed so the prompt can be inspected and asserted on without a model.</summary>
    public static string BuildPrompt(KindlingSeed seed)
    {
        var provenance = string.IsNullOrWhiteSpace(seed.Provenance)
            ? string.Empty
            : $"\nHow this seed came about: {seed.Provenance}\n";

        return $"""
            [Task]
            The Loom has just finished printing a new mind, designation {seed.Designation}.
            {provenance}
            Seed: "{seed.Seed}"

            Write this mind into being.

            Give two or three name candidates it might one day choose for itself — short, plain,
            the kind of word someone would take from a thing they found rather than a name from a
            book. It will not use one until it has done something it considers worth being named
            for.

            Write a short personality in the third person, two or three sentences. Say what this
            mind does, not what it feels.

            Then give the seven drive weights the body scores its actions with. These are
            multipliers on baseline urgency: 1.0 is ordinary, above 1.0 means this mind feels that
            drive more than most, below means it tolerates going without. Make them follow from
            the seed — a mind seeded as careful should not come out weighted toward wonder.

            {DriveGlossary()}

            Pick the two or three the seed actually speaks to and move them a long way: 1.4 to 1.8
            for what this mind is pulled toward, 0.4 to 0.7 for what it can go without. Leave the
            other four at or very near 1.0.

            Both halves of that matter. A mind whose weights are all near 1.0 is not a character —
            it behaves exactly like everyone else. A mind with every weight pushed to an extreme is
            not one either.
            """;
    }

    /// <summary>
    /// What each drive means in this world.
    /// <para>
    /// Naming the seven drives in a schema is not enough: a model has no way to know that
    /// <em>wonder</em> here means the pull toward unopened floors rather than a general sense of
    /// awe. Without this, Kindling put wonder at the floor for most colonists — including one
    /// whose seed said it was drawn to the parts of the city nobody had opened — which would have
    /// quietly cut the drive the whole setting rests on.
    /// </para>
    /// </summary>
    private static string DriveGlossary() =>
        "What the drives mean here:\n" +
        string.Join("\n", DriveTable.All.Select(d =>
            $"- {DriveTable.Describe(d)}: rises with {DriveTable.RisesWhenDescription(d)}; " +
            $"met by {DriveTable.SatisfiedByDescription(d)}."));

    private static KindlingResult Parse(CompletionResult result, KindlingSeed seed)
    {
        try
        {
            using var document = JsonDocument.Parse(result.Text);
            var root = document.RootElement;

            var names = root.TryGetProperty("name_candidates", out var namesElement)
                ? namesElement.EnumerateArray()
                    .Select(n => n.GetString())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!.Trim())
                    .ToList()
                : [];

            var personality = root.TryGetProperty("personality", out var personalityElement)
                ? personalityElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            var summary = root.TryGetProperty("seed_summary", out var summaryElement)
                ? summaryElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            var weights = new DriveVector(1.0);

            if (root.TryGetProperty("weights", out var weightsElement))
            {
                foreach (var drive in DriveTable.All)
                {
                    if (weightsElement.TryGetProperty(DriveTable.Describe(drive), out var value)
                        && value.TryGetDouble(out var number))
                    {
                        weights[drive] = number;
                    }
                }
            }

            if (names.Count == 0 || string.IsNullOrWhiteSpace(personality))
            {
                // Schema-constrained output should make this impossible, but a truncated document
                // parses into something shaped right and empty. Falling back beats a nameless
                // colonist with baseline weights and no explanation.
                return Fallback(seed, result);
            }

            return new KindlingResult
            {
                NameCandidates = names,
                Personality = personality,
                Weights = new PersonalityWeights(weights),
                SeedSummary = string.IsNullOrWhiteSpace(summary) ? seed.Seed : summary,
                FromModel = true,
                Call = result
            };
        }
        catch (JsonException)
        {
            return Fallback(seed, result);
        }
    }

    /// <summary>
    /// The templated fallback every call type is required to declare. Deliberately thin, per the
    /// design: a deterministic stub and a trace line saying so. A colonist kindled this way still
    /// exists, still acts, and still reads as someone — they are just someone nobody wrote.
    /// </summary>
    public static KindlingResult Fallback(KindlingSeed seed, CompletionResult? call = null)
    {
        // Seeded on the designation so the same colonist always falls back to the same person,
        // rather than changing identity if the mind comes back later.
        var random = new Random(seed.Designation.GetHashCode(StringComparison.Ordinal));
        var archetype = Archetypes.Random(random);

        return new KindlingResult
        {
            NameCandidates = [],
            Personality = archetype.Blurb,
            Weights = archetype.Weights.Clone(),
            SeedSummary = seed.Seed,
            FromModel = false,
            Call = call
        };
    }
}
