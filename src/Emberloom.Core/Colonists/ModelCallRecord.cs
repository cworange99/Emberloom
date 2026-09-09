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
    public required string CallType { get; init; }

    public required double Day { get; init; }

    public required string Model { get; init; }

    public required string Lane { get; init; }

    public required int PromptTokens { get; init; }

    public required int CompletionTokens { get; init; }

    public required double Seconds { get; init; }

    /// <summary>What the call actually changed. This is the line the Trace tab reads.</summary>
    public required string Produced { get; init; }

    /// <summary>
    /// True when the mind was unavailable and a deterministic stub stood in. The trace has to
    /// show these plainly — a colony that quietly ran on fallbacks would otherwise look like a
    /// colony that thought.
    /// </summary>
    public bool WasFallback { get; init; }

    public int TotalTokens => PromptTokens + CompletionTokens;
}
