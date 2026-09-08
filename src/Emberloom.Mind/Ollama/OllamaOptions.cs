namespace Emberloom.Mind.Ollama;

/// <summary>
/// Which physical model backs a logical lane. The mind has two: <c>deep</c> for calls that
/// write something durable, <c>fast</c> for idle thought that writes nothing.
/// </summary>
public sealed record LaneConfig
{
    public required string Model { get; init; }

    /// <summary>Context window. Sized per lane so the fast lane does not pay for the deep lane's KV cache.</summary>
    public int NumCtx { get; init; } = 4096;

    /// <summary>
    /// How long Ollama keeps the model resident after a call. Long, deliberately: a reload
    /// mid-simulation costs more than the call it serves.
    /// </summary>
    public string KeepAlive { get; init; } = "30m";

    /// <summary>
    /// Whether the model may spend tokens on visible reasoning before answering.
    /// <para>
    /// Off by default, and that is not a stylistic preference — on a thinking-capable model
    /// Ollama counts reasoning tokens against <c>num_predict</c> but returns them in a separate
    /// field, so the token cap silently buys reasoning instead of the answer. Measured on
    /// gemma4:12b: a one-word reply cost 38 tokens with thinking on and 2 with it off, and every
    /// schema-constrained call in the first Target benchmark run came back truncated or empty.
    /// </para>
    /// <para>
    /// The design's per-call caps are sized for the artifact a call produces, so reasoning has
    /// to be off for those caps to mean what §5 says they mean. Sending this to a model with no
    /// thinking mode is harmless.
    /// </para>
    /// </summary>
    public bool? Think { get; init; } = false;
}

public sealed record OllamaOptions
{
    public Uri Endpoint { get; init; } = new("http://localhost:11434");

    public required LaneConfig Deep { get; init; }

    public required LaneConfig Fast { get; init; }

    public string EmbeddingModel { get; init; } = "nomic-embed-text";

    /// <summary>Ceiling on a single call. The governor budgets throughput; this only catches a hang.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

public enum Lane
{
    Deep,
    Fast
}
