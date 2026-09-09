using Emberloom.Core.Colonists;
using Emberloom.Core.Events;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Ollama;

namespace Emberloom.Mind;

/// <summary>
/// Layer 2's entry point. Owns the transport, decides whether the mind is available at all, and
/// writes every call's outcome back into the body as a durable artifact plus a trace line.
/// <para>
/// It does not own a queue or a budget — that is the governor's job, and the governor is M2. What
/// this class establishes is the seam: a call goes out, something durable comes back, and the body
/// reads it on every subsequent tick without inference.
/// </para>
/// </summary>
public sealed class MindService : IDisposable
{
    private readonly OllamaClient _client;
    private readonly bool _ownsClient;

    public MindService(OllamaOptions options)
        : this(new OllamaClient(options), ownsClient: true)
    {
    }

    public MindService(OllamaClient client, bool ownsClient = false)
    {
        _client = client;
        _ownsClient = ownsClient;
    }

    public OllamaClient Client => _client;

    /// <summary>
    /// Whether Layer 2 is reachable. Re-checked rather than cached at startup, because the design
    /// requires the game to keep running when Ollama goes away mid-session and to pick it back up
    /// when it returns.
    /// </summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => _client.IsAvailableAsync(ct);

    /// <summary>
    /// Kindles a colonist: gives them the personality and the seven weights the body scores with.
    /// Safe to call on an unreachable mind — the colonist gets the deterministic stub and a trace
    /// line saying as much, and keeps acting either way.
    /// </summary>
    public async Task<KindlingResult> KindleAsync(
        Colonist colonist,
        Colony colony,
        string seedText,
        string? provenance = null,
        CancellationToken ct = default)
    {
        var seed = new KindlingSeed
        {
            Designation = colonist.Designation,
            Seed = seedText,
            Provenance = provenance
        };

        KindlingResult result;

        try
        {
            result = await KindlingCall.ExecuteAsync(_client, seed, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Only a cancellation the caller asked for. A request timeout also arrives as an
            // OperationCanceledException, and that one is a fallback, not a fault — it is in
            // fact the likeliest way for the mind to be unavailable: a busy GPU, a model still
            // loading, a remote endpoint that hiccuped.
            throw;
        }
        catch (Exception)
        {
            // The mind being down is an ordinary condition, not an error. This is the path the
            // whole architecture leans on, so it must never surface as a failure to the caller.
            result = KindlingCall.Fallback(seed);
        }

        Apply(colonist, colony, result);
        return result;
    }

    /// <summary>
    /// Writes a kindling result into the body. This is the seam in one method: after this returns,
    /// the colonist scores every future action against weights a model wrote, with no further
    /// inference.
    /// </summary>
    private static void Apply(Colonist colonist, Colony colony, KindlingResult result)
    {
        colonist.Personality = result.Personality;
        colonist.Weights = result.Weights;
        colonist.NameCandidates = result.NameCandidates;
        colonist.IsKindled = result.FromModel;

        var produced = result.FromModel
            ? $"personality and weights · {WeightSummary(result)}"
            : "fallback stub — the mind was unavailable";

        colonist.RecordCall(new ModelCallRecord
        {
            CallType = "Kindling",
            Day = colony.Day,
            Model = result.Call?.Model ?? "none",
            Lane = "deep",
            PromptTokens = result.Call?.PromptTokens ?? 0,
            CompletionTokens = result.Call?.CompletionTokens ?? 0,
            Seconds = result.Call?.TotalDuration.TotalSeconds ?? 0,
            Produced = produced,
            WasFallback = !result.FromModel
        });

        colony.Log(
            result.FromModel ? EventLayer.Mind : EventLayer.World,
            result.FromModel
                ? $"{colonist.Designation} woke. {FirstSentence(result.Personality)}"
                : $"{colonist.Designation} woke, but nothing was there to write them.",
            colonist,
            notable: true);
    }

    private static string WeightSummary(KindlingResult result) =>
        Prompts.WorldPrompt.Weights(result.Weights);

    private static string FirstSentence(string text)
    {
        var trimmed = text.Trim();
        var stop = trimmed.IndexOf(". ", StringComparison.Ordinal);
        return stop > 0 ? trimmed[..(stop + 1)] : trimmed;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
