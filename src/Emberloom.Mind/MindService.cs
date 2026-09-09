using Emberloom.Core.Colonists;
using Emberloom.Core.Events;
using Emberloom.Core.Memory;
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

    /// <summary>
    /// Runs a reflection: reads the colonist's most salient unconsolidated memories and writes
    /// back what they meant. Safe on an unreachable mind — the batch gets marked consolidated
    /// either way, so a colony without a mind does not accumulate an unbounded backlog of
    /// experience waiting to be understood.
    /// </summary>
    public async Task<ReflectionResult> ReflectAsync(
        Colonist colonist,
        Colony colony,
        CancellationToken ct = default)
    {
        var batch = colonist.Memory.ReflectionBatch(colony.Day);
        ReflectionResult result;

        try
        {
            result = await ReflectionCall.ExecuteAsync(_client, colonist, colony, batch, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            result = ReflectionCall.Fallback(batch);
        }

        await ApplyReflectionAsync(colonist, colony, batch, result, ct).ConfigureAwait(false);
        return result;
    }

    private async Task ApplyReflectionAsync(
        Colonist colonist,
        Colony colony,
        IReadOnlyList<EpisodicMemory> batch,
        ReflectionResult result,
        CancellationToken ct)
    {
        var consolidated = new ConsolidatedMemory
        {
            Id = $"cm-{colonist.Id}-{colonist.Memory.Consolidated.Count + 1}",
            OwnerId = colonist.Id,
            Day = colony.Day,
            Text = result.ConsolidatedText,
            Valence = result.Valence,
            SourceMemoryIds = result.SourceMemoryIds
        };

        colonist.Memory.Add(consolidated);

        // The batch is marked consolidated whether or not a mind was there. Leaving it unmarked
        // would make the same rows resurface forever and starve everything newer.
        foreach (var memory in batch)
        {
            memory.Consolidated = true;
        }

        var beliefs = new List<Belief>();

        foreach (var draft in result.Beliefs)
        {
            var belief = new Belief
            {
                Id = $"b-{colonist.Id}-{colonist.Memory.Beliefs.Count + beliefs.Count + 1}",
                OwnerId = colonist.Id,
                FormedOnDay = colony.Day,
                Claim = draft.Claim,
                SourceMemoryIds = draft.SourceMemoryIds,
                Confidence = draft.Confidence
            };

            beliefs.Add(belief);
            colonist.Memory.Add(belief);
        }

        // Weight drift is the second place a model call permanently changes behaviour, after
        // Kindling. It is deliberately small and cumulative rather than a rewrite.
        foreach (var (drive, amount) in result.WeightDrift)
        {
            colonist.Weights.Drift(drive, amount);
        }

        if (result.RelationshipAbout is { Length: > 0 } about
            && result.RelationshipReading is { Length: > 0 } reading)
        {
            var other = colony.Living.FirstOrDefault(c =>
                c.Id != colonist.Id
                && (c.Designation.Equals(about, StringComparison.OrdinalIgnoreCase)
                    || c.DisplayName.Equals(about, StringComparison.OrdinalIgnoreCase)));

            if (other is not null)
            {
                colony.Relationships.Get(colonist.Id, other.Id).Narrative = reading;
            }
        }

        await EmbedAsync(consolidated, beliefs, ct).ConfigureAwait(false);

        colonist.RecordCall(new ModelCallRecord
        {
            CallType = "Reflection",
            Day = colony.Day,
            Model = result.Call?.Model ?? "none",
            Lane = "deep",
            PromptTokens = result.Call?.PromptTokens ?? 0,
            CompletionTokens = result.Call?.CompletionTokens ?? 0,
            Seconds = result.Call?.TotalDuration.TotalSeconds ?? 0,
            Produced = result.FromModel
                ? Describe(beliefs.Count, batch.Count, result.WeightDrift.Count)
                : "fallback stub — the mind was unavailable",
            WasFallback = !result.FromModel
        });

        foreach (var belief in beliefs)
        {
            colony.Log(EventLayer.Mind,
                $"{colonist.DisplayName} came to believe: “{belief.Claim}”",
                colonist, notable: true);
        }
    }

    /// <summary>
    /// Embeds what reflection produced, so retrieval can find it later.
    /// <para>
    /// The call is stateless — text goes out, a vector comes back, and the server keeps nothing.
    /// The vectors are stored here, on the colonist, and searched locally. That split is what the
    /// golden rule requires, and retrieval is the likeliest place in the whole design to break it.
    /// </para>
    /// </summary>
    private async Task EmbedAsync(
        ConsolidatedMemory consolidated,
        IReadOnlyList<Belief> beliefs,
        CancellationToken ct)
    {
        try
        {
            consolidated.Embedding = await _client.EmbedAsync(consolidated.Text, ct).ConfigureAwait(false);

            foreach (var belief in beliefs)
            {
                belief.Embedding = await _client.EmbedAsync(belief.Claim, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Unembedded rows are simply invisible to retrieval. They are still readable in the
            // inspector and still inherited, so losing an embedding costs recall, not the memory.
        }
    }

    private static string Describe(int beliefs, int sources, int drifted)
    {
        var parts = new List<string> { $"1 consolidated memory from {sources} episodic rows" };

        if (beliefs > 0)
        {
            parts.Add($"{beliefs} belief{(beliefs == 1 ? "" : "s")}");
        }

        if (drifted > 0)
        {
            parts.Add($"drift on {drifted} weight{(drifted == 1 ? "" : "s")}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Idle thought on the fast lane. Writes nothing durable; failure is simply silence.</summary>
    public async Task<string?> ThinkAsync(Colonist colonist, Colony colony, CancellationToken ct = default)
    {
        var thought = await IdleThoughtCall.ExecuteAsync(_client, colonist, colony, ct).ConfigureAwait(false);

        if (thought is not null)
        {
            colonist.LastThought = thought;
        }

        return thought;
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
