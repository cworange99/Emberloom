using System.Text.Json;
using Emberloom.Core.Colonists;
using Emberloom.Core.Culture;
using Emberloom.Mind.Ollama;
using Emberloom.Mind.Prompts;

namespace Emberloom.Mind.Calls;

public sealed record CoinageResult
{
    /// <summary>The word, or null when nothing usable came back and no word stuck.</summary>
    public string? Term { get; init; }

    public string Meaning { get; init; } = string.Empty;

    public required bool FromModel { get; init; }

    public CompletionResult? Call { get; init; }
}

/// <summary>
/// Coinage — a name for something found, or for something the colony keeps doing. Short and
/// cheap, and it lasts: the word goes into every prompt afterwards and is reused verbatim, which
/// is how a colony comes to sound like itself.
/// </summary>
public static class CoinageCall
{
    public static async Task<CoinageResult> ExecuteAsync(
        OllamaClient client,
        Colonist colonist,
        Colony colony,
        Occasion occasion,
        CancellationToken ct = default)
    {
        var request = new CompletionRequest
        {
            System = WorldPrompt.SystemFor(colonist, colony),
            Prompt = BuildPrompt(colonist, colony, occasion),
            Schema = MindSchemas.Coinage(),
            MaxTokens = CallBudgets.For(CallType.Coinage),
            Temperature = 0.9,
            Lane = Lane.Deep
        };

        var result = await client.CompleteAsync(request, ct).ConfigureAwait(false);
        return Parse(result);
    }

    public static string BuildPrompt(Colonist colonist, Colony colony, Occasion occasion)
    {
        var name = colonist.DisplayName;

        // Every find is the same kind of thing. Asked each time for "the name for this kind of
        // thing", the colony coined "Remnant" and then "maker's fragment" for it live at M7 — two
        // words for one idea. The first find names the kind; every later one names the piece.
        var kind = colony.Lexicon.FirstOrDefault(w => w.Occasion == OccasionKind.Artifact);

        var (what, ask) = occasion.Kind switch
        {
            OccasionKind.Artifact when kind is not null => (
                $"{name} has just found something of the makers' at {occasion.About}. The colony calls such things “{kind.Term}”, but nobody knows what this one is or what it was for.",
                $"the name this one piece will be known by — not “{kind.Term}” or any word for things like it, but a name for this one"),
            OccasionKind.Artifact => (
                $"{name} has just found something of the makers' at {occasion.About}. Nobody knows what it is or what it was for.",
                "the name the colony will use for this kind of thing"),
            _ => (
                $"{name} keeps doing the same thing: {occasion.About}. It has stopped being something that happens and become something they do.",
                "the name the colony will use for this practice")
        };

        return $"""
            [Occasion]
            {what}

            [Task]
            Coin {ask}: short and plain, one to three words, the kind of name someone gives a thing
            they found rather than one out of a book, and not one of the colony's words already.
            Then one sentence on what in the colony's life the name comes out of.
            """;
    }

    public static CoinageResult Parse(CompletionResult result)
    {
        try
        {
            using var document = JsonDocument.Parse(result.Text);
            var root = document.RootElement;

            var term = (root.TryGetProperty("name", out var n) ? n.GetString() : null)?
                .Trim().Trim('"', '“', '”', '\'', '.').Trim();

            var meaning = root.TryGetProperty("rationale", out var r) ? r.GetString()?.Trim() ?? string.Empty : string.Empty;

            // A coinage is a word or two. A sentence where the word should be is a model
            // explaining rather than naming, and would read as nonsense reused verbatim.
            if (string.IsNullOrWhiteSpace(term) || term.Length > 40 || term.Split(' ').Length > 5)
            {
                return Fallback(result);
            }

            return new CoinageResult { Term = term, Meaning = meaning, FromModel = true, Call = result };
        }
        catch (JsonException)
        {
            return Fallback(result);
        }
    }

    /// <summary>No word. A colony running on stubs does not coin anything, and never looks like it did.</summary>
    public static CoinageResult Fallback(CompletionResult? call = null) => new() { FromModel = false, Call = call };
}
