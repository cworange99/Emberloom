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

    public required double Seconds { get; init; }

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
