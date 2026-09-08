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
