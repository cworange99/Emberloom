namespace Emberloom.Core.Colonists;

/// <summary>
/// One entry in a mind's model trace — what was asked, what it cost, and what it produced.
/// <para>
/// Lives in Core rather than beside the call types so that <see cref="Colonist"/> can hold it
/// without Core taking a dependency on the mind layer. Core stays free of model code; that is
/// the whole point of the two-layer split.
/// </para>
/// </summary>
public sealed record ModelCallRecord
{
    /// <summary>
    /// Unique within one colonist's trace. Beliefs and consolidated memories carry the id of the call
    /// that formed them, which is what lets the inspector walk from a belief back to the exact call —
    /// prompt, response and all — that produced it. Assigned by <see cref="Colonist.RecordCall"/> when
    /// left empty.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    public required string CallType { get; init; }

    public required double Day { get; init; }

    public required string Model { get; init; }

    public required string Lane { get; init; }

    public required int PromptTokens { get; init; }

    public required int CompletionTokens { get; init; }

    /// <summary>
    /// Wall-clock for the whole call, as the inference server reports it — and **not** a measure of
    /// how fast the model is.
    /// <para>
    /// Measured 24 October 2026, chasing a vote that took four minutes: a deliberation's recorded
    /// rate was 1.06 tokens a second while the same model on the same prompt generated at 9.4. The
    /// gap is time the request spent inside the server neither loading, reading the prompt nor
    /// writing — waiting its turn. Dividing completion tokens by this number therefore measures
    /// congestion and calls it speed, which is how a perfectly healthy call type came to look six
    /// times slower than its neighbours.
    /// </para>
    /// <para>
    /// The three numbers below are the breakdown, and they are what any claim about speed has to
    /// be made from. The server has always reported them; the trace used to throw them away.
    /// </para>
    /// </summary>
    public required double Seconds { get; init; }

    /// <summary>Loading the model. Non-zero means <c>keep_alive</c> had expired.</summary>
    public double LoadSeconds { get; init; }

    /// <summary>Reading the prompt. Falls to almost nothing when the KV prefix cache hits.</summary>
    public double PrefillSeconds { get; init; }

    /// <summary>Writing the answer. This, over the completion tokens, is the model's real speed.</summary>
    public double GenerateSeconds { get; init; }

    /// <summary>
    /// What the call spent neither loading, reading nor writing — queued behind something else.
    /// A large figure here is a busy inference server, not a slow one.
    /// </summary>
    public double WaitingSeconds =>
        Math.Max(0, Seconds - LoadSeconds - PrefillSeconds - GenerateSeconds);

    /// <summary>What the call actually changed. This is the line the Trace tab reads.</summary>
    public required string Produced { get; init; }

    /// <summary>Ids of the durable artifacts this call wrote — consolidated memories, beliefs.</summary>
    public IReadOnlyList<string> ProducedIds { get; init; } = [];

    /// <summary>
    /// True when the mind was unavailable and a deterministic stub stood in. The trace has to
    /// show these plainly — a colony that quietly ran on fallbacks would otherwise look like a
    /// colony that thought.
    /// </summary>
    public bool WasFallback { get; init; }

    /// <summary>
    /// What was actually sent and what came back. Kept here, on the colonist — the server is never
    /// asked to remember any of it (the golden rule; see CLAUDE.md). Only the most recent calls keep
    /// their text, so a long run does not grow without bound; older records keep everything else.
    /// See <see cref="Colonist.TraceTextRetention"/>.
    /// </summary>
    public string? System { get; init; }

    public string? Prompt { get; init; }

    public string? Response { get; init; }

    public bool HasText => Prompt is not null || Response is not null;

    public int TotalTokens => PromptTokens + CompletionTokens;
}
