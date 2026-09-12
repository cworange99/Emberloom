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
/// It does not own a queue or a budget — that is the governor's job. What this class establishes
/// is the seam: a call goes out, something durable comes back, and the body reads it on every
/// subsequent tick without inference.
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

    /// <summary>How much a belief's confidence rises when a later reflection arrives at it again.</summary>
    private const double ReinforcementLift = 0.05;

    /// <summary>
    /// Runs a write-back against the colony. Inference happens on the mind's own thread, but what it
    /// writes lands in state the tick loop mutates and the API reads, so the host supplies its lock
    /// here. The default runs the work directly, which is right for a single-threaded caller such as
    /// a test.
    /// <para>
    /// Only the write-back goes through this — never the call itself. Holding the colony across a
    /// multi-second inference would stall the simulation, which Layer 2 is never allowed to do.
    /// </para>
    /// </summary>
    public Action<Action> Commit { get; init; } = static work => work();

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

        ApplyKindling(colonist, colony, result);
        return result;
    }

    /// <summary>
    /// Writes a kindling result into the body. This is the seam in one method: after this returns,
    /// the colonist scores every future action against weights a model wrote, with no further
    /// inference.
    /// </summary>
    private void ApplyKindling(Colonist colonist, Colony colony, KindlingResult result)
    {
        var names = result.NameCandidates.Count;
        var produced = result.FromModel
            ? $"personality, {names} name candidate{(names == 1 ? "" : "s")}, seven weights · {WeightSummary(result)}"
            : "fallback stub — the mind was unavailable";

        Commit(() =>
        {
            colonist.Personality = result.Personality;
            colonist.Weights = result.Weights;
            colonist.NameCandidates = result.NameCandidates;
            colonist.IsKindled = result.FromModel;

            colonist.RecordCall(Record(
                string.Empty, "Kindling", "deep", colony, result.Call, produced, wasFallback: !result.FromModel));

            colony.Log(
                result.FromModel ? EventLayer.Mind : EventLayer.World,
                result.FromModel
                    ? $"{colonist.Designation} woke. {result.SeedSummary}"
                    : $"{colonist.Designation} woke, but nothing was there to write them.",
                colonist,
                notable: true);
        });
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
        // Read under the same lock as the write-back: the tick loop is appending to this store.
        IReadOnlyList<EpisodicMemory> batch = [];
        Commit(() => batch = colonist.Memory.ReflectionBatch(colony.Day));

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
        // Reserved before anything is written, so everything this reflection produces can cite the
        // call that produced it. That citation is the last link in the inspector's chain: belief,
        // to the memories it came from, to the exact prompt and response that formed it.
        var callId = colonist.NextCallId();

        var consolidated = new ConsolidatedMemory
        {
            Id = $"cm-{colonist.Id}-{colonist.Memory.Consolidated.Count + 1}",
            OwnerId = colonist.Id,
            Day = colony.Day,
            Text = result.ConsolidatedText,
            Valence = result.Valence,
            SourceMemoryIds = result.SourceMemoryIds,
            FormedByCallId = callId
        };

        var beliefs = result.Beliefs
            .Select((draft, index) => new Belief
            {
                Id = $"b-{colonist.Id}-{colonist.Memory.Beliefs.Count + index + 1}",
                OwnerId = colonist.Id,
                FormedOnDay = colony.Day,
                Claim = draft.Claim,
                SourceMemoryIds = draft.SourceMemoryIds,
                Confidence = draft.Confidence,
                FormedByCallId = callId
            })
            .ToList();

        // Embedding is a network call, so it happens before the write-back rather than under the
        // host's lock. Nothing else can see these rows until they are added, so filling in their
        // vectors first is safe.
        await EmbedAsync(consolidated, beliefs, ct).ConfigureAwait(false);

        Commit(() =>
        {
            colonist.Memory.Add(consolidated);

            // The batch is marked consolidated whether or not a mind was there. Leaving it unmarked
            // would make the same rows resurface forever and starve everything newer.
            foreach (var memory in batch)
            {
                memory.Consolidated = true;
            }

            // A claim this mind already holds reinforces the belief it restates instead of sitting
            // beside it as a second conviction. Measured at M4: one mind in a laptop colony held
            // "Trust is earned through actions, not just interactions" and "…not just presence" as
            // two beliefs. What counts as a restatement is calibrated, and deliberately strict — see
            // BeliefSimilarity. The held belief keeps its
            // id and wording, so every chain already pointing at it still resolves; it gains the
            // new sources, a little confidence, and a record of which call came back to it.
            var formed = new List<Belief>();
            var reinforced = new List<Belief>();

            foreach (var belief in beliefs)
            {
                var held = colonist.Memory.Beliefs.FirstOrDefault(b => BeliefSimilarity.Restates(belief, b));

                if (held is null)
                {
                    colonist.Memory.Add(belief);
                    formed.Add(belief);
                    continue;
                }

                var stronger = held with
                {
                    SourceMemoryIds = held.SourceMemoryIds.Union(belief.SourceMemoryIds).ToList(),
                    Confidence = Math.Min(1.0, Math.Max(held.Confidence, belief.Confidence) + ReinforcementLift),
                    ReinforcedByCallIds = held.ReinforcedByCallIds.Append(callId).ToList()
                };

                colonist.Memory.Replace(held, stronger);
                reinforced.Add(stronger);
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

            var produced = result.FromModel
                ? Describe(formed.Count, reinforced.Count, batch.Count, result.WeightDrift.Count)
                : "fallback stub — the mind was unavailable";

            colonist.RecordCall(Record(
                callId, "Reflection", "deep", colony, result.Call, produced,
                wasFallback: !result.FromModel,
                producedIds: formed.Concat(reinforced).Select(b => b.Id).Prepend(consolidated.Id).ToList()));

            foreach (var belief in formed)
            {
                colony.Log(EventLayer.Mind,
                    $"{colonist.DisplayName} came to believe: “{belief.Claim}”",
                    colonist, notable: true);
            }

            // Worth a line, not a flag: holding something more firmly is quieter than coming to
            // believe it.
            foreach (var belief in reinforced)
            {
                colony.Log(EventLayer.Mind,
                    $"{colonist.DisplayName} holds it more firmly now: “{belief.Claim}”",
                    colonist);
            }
        });
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

    private static string Describe(int formed, int reinforced, int sources, int drifted)
    {
        var parts = new List<string> { $"1 consolidated memory from {sources} episodic rows" };

        if (formed > 0)
        {
            parts.Add($"{formed} new belief{(formed == 1 ? "" : "s")}");
        }

        if (reinforced > 0)
        {
            parts.Add($"{reinforced} belief{(reinforced == 1 ? "" : "s")} reinforced");
        }

        if (drifted > 0)
        {
            parts.Add($"drift on {drifted} weight{(drifted == 1 ? "" : "s")}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Idle thought on the fast lane. Writes nothing durable, but it is still a call this mind made,
    /// and the Trace tab's promise is every call — idle thought is most of them. A thought that never
    /// arrived because there was no mind is silence and leaves no line; a call that came back empty
    /// still spent tokens, so it does.
    /// </summary>
    public async Task<(string? Thought, CompletionResult? Call)> ThinkAsync(
        Colonist colonist,
        Colony colony,
        CancellationToken ct = default)
    {
        var (thought, call) = await IdleThoughtCall.ExecuteAsync(_client, colonist, colony, ct).ConfigureAwait(false);

        if (call is null)
        {
            return (null, null);
        }

        Commit(() =>
        {
            if (thought is not null)
            {
                colonist.LastThought = thought;
            }

            colonist.RecordCall(Record(
                string.Empty, "IdleThought", "fast", colony, call,
                thought is null ? "nothing that came to words" : $"“{thought}”",
                wasFallback: false));
        });

        return (thought, call);
    }

    /// <summary>
    /// Two colonists talking: what one says and the other's reply. The words go into the feed, over
    /// the speakers' heads, and onto both of their memories of the conversation — so when they next
    /// reflect, they reflect on what was actually said.
    /// </summary>
    public async Task<DialogueResult> DialogueAsync(
        Colonist speaker,
        Colonist listener,
        Colony colony,
        CancellationToken ct = default)
    {
        DialogueResult result;

        try
        {
            result = await DialogueCall.ExecuteAsync(_client, speaker, listener, colony, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            result = DialogueCall.Fallback(speaker, listener);
        }

        string NameOf(string id) => id == speaker.Id ? speaker.DisplayName : listener.DisplayName;
        var exchange = string.Join(" / ", result.Lines.Select(l => $"{NameOf(l.SpeakerId)}: “{l.Text}”"));

        Commit(() =>
        {
            // One exchange with two minds in it, so it goes in both traces.
            foreach (var participant in new[] { speaker, listener })
            {
                participant.RecordCall(Record(
                    string.Empty, "Dialogue", "deep", colony, result.Call, exchange, wasFallback: !result.FromModel));
            }

            foreach (var line in result.Lines)
            {
                var by = line.SpeakerId == speaker.Id ? speaker : listener;
                var to = ReferenceEquals(by, speaker) ? listener : speaker;

                // Stock lines are not two minds talking, so they go out as the world's, not the mind's.
                colony.Log(
                    result.FromModel ? EventLayer.Mind : EventLayer.World,
                    $"{by.DisplayName} to {to.DisplayName}: “{line.Text}”",
                    by,
                    quote: line.Text);
            }

            // Only real words are remembered. A colony running on stubs should not reflect on its
            // own stock lines as though someone had meant them.
            if (result.FromModel)
            {
                Remember(speaker, listener, exchange, colony.Day);
                Remember(listener, speaker, exchange, colony.Day);
            }
        });

        return result;
    }

    /// <summary>Puts the words on the conversation's memory, or holds them until the body writes it.</summary>
    private static void Remember(Colonist colonist, Colonist other, string words, double day)
    {
        if (!colonist.Memory.AttachWords(other.Id, words, day))
        {
            colonist.PendingWords[other.Id] = words;
        }
    }

    /// <summary>
    /// One trace line. The request and response texts ride along from the completion so the Trace
    /// tab can show exactly what was asked; they stay on this machine, in the colonist's trace.
    /// </summary>
    private static ModelCallRecord Record(
        string id,
        string callType,
        string lane,
        Colony colony,
        CompletionResult? call,
        string produced,
        bool wasFallback,
        IReadOnlyList<string>? producedIds = null) => new()
    {
        Id = id,
        CallType = callType,
        Day = colony.Day,
        Model = call?.Model ?? "none",
        Lane = lane,
        PromptTokens = call?.PromptTokens ?? 0,
        CompletionTokens = call?.CompletionTokens ?? 0,
        Seconds = call?.TotalDuration.TotalSeconds ?? 0,
        Produced = produced,
        ProducedIds = producedIds ?? Array.Empty<string>(),
        WasFallback = wasFallback,
        System = call?.RequestSystem,
        Prompt = call?.RequestPrompt,
        Response = call?.Text
    };

    private static string WeightSummary(KindlingResult result) =>
        Prompts.WorldPrompt.Weights(result.Weights);

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
