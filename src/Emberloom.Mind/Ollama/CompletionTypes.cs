using System.Text.Json.Nodes;

namespace Emberloom.Mind.Ollama;

/// <summary>
/// One call to the mind. Prompt is assembled stable-prefix-first so Ollama's KV cache can
/// reuse the expensive part across calls: <see cref="System"/> holds world rules and the
/// colonist's personality, <see cref="Prompt"/> holds only what changed.
/// </summary>
public sealed record CompletionRequest
{
    public required string System { get; init; }

    public required string Prompt { get; init; }

    /// <summary>
    /// A JSON schema passed through to Ollama's <c>format</c> parameter. Every call type except
    /// Dialogue sets one — it buys reliable parsing and a hard token ceiling from small models.
    /// </summary>
    public JsonNode? Schema { get; init; }

    /// <summary>Hard cap on generated tokens (<c>num_predict</c>). Per the per-call-type budget.</summary>
    public int MaxTokens { get; init; } = 256;

    public double Temperature { get; init; } = 0.8;

    public Lane Lane { get; init; } = Lane.Deep;
}

/// <summary>
/// A completion plus the measurements the governor and the inspector's Trace tab both need.
/// Durations come straight from Ollama's nanosecond counters.
/// </summary>
public sealed record CompletionResult
{
    public required string Model { get; init; }

    public required string Text { get; init; }

    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }

    public TimeSpan TotalDuration { get; init; }

    /// <summary>Time spent loading the model. Non-zero here means <c>keep_alive</c> expired.</summary>
    public TimeSpan LoadDuration { get; init; }

    public TimeSpan PromptEvalDuration { get; init; }

    public TimeSpan EvalDuration { get; init; }

    /// <summary>Generation throughput, excluding load and prompt evaluation.</summary>
    public double TokensPerSecond =>
        EvalDuration > TimeSpan.Zero ? CompletionTokens / EvalDuration.TotalSeconds : 0;

    /// <summary>Wall-clock throughput including load and prompt evaluation — what the budget actually spends.</summary>
    public double EffectiveTokensPerSecond =>
        TotalDuration > TimeSpan.Zero ? CompletionTokens / TotalDuration.TotalSeconds : 0;
}

public sealed class OllamaException : Exception
{
    public OllamaException(string message, Exception? inner = null) : base(message, inner) { }
}
