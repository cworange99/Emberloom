using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Emberloom.Mind.Ollama;

/// <summary>
/// Thin transport over Ollama's HTTP API. Knows nothing about colonists — it takes a
/// <see cref="CompletionRequest"/> and reports what it cost. Everything above it (the five
/// call types, the governor) is built on this and nothing else.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly OllamaOptions _options;
    private readonly bool _ownsHttpClient;

    public OllamaClient(OllamaOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttpClient = http is null;
        _http = http ?? new HttpClient();
        _http.BaseAddress = options.Endpoint;
        _http.Timeout = options.RequestTimeout;
    }

    public OllamaOptions Options => _options;

    public LaneConfig LaneConfig(Lane lane) => lane == Lane.Fast ? _options.Fast : _options.Deep;

    /// <summary>
    /// True if Ollama is reachable. The simulation runs regardless; this only decides whether
    /// Layer 2 is admitted or every call type falls back to its templated stub.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("/api/tags", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var payload = await _http.GetFromJsonAsync<TagsResponse>("/api/tags", ct).ConfigureAwait(false);
        return payload?.Models?.Select(m => m.Name).ToList() ?? new List<string>();
    }

    /// <summary>
    /// Loads a model into memory without generating anything, so the first real call of a
    /// session does not pay the load cost. Ollama treats an empty prompt as a load request.
    /// </summary>
    public async Task WarmAsync(Lane lane, CancellationToken ct = default)
    {
        var config = LaneConfig(lane);
        var body = new GenerateRequest
        {
            Model = config.Model,
            Prompt = string.Empty,
            Stream = false,
            KeepAlive = config.KeepAlive
        };

        using var response = await _http.PostAsJsonAsync("/api/generate", body, SerializerOptions, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        var config = LaneConfig(request.Lane);

        var body = new GenerateRequest
        {
            Model = config.Model,
            System = string.IsNullOrEmpty(request.System) ? null : request.System,
            Prompt = request.Prompt,
            Stream = false,
            KeepAlive = config.KeepAlive,
            Format = request.Schema,
            Options = new GenerateOptions
            {
                NumPredict = request.MaxTokens,
                Temperature = request.Temperature,
                NumCtx = config.NumCtx
            }
        };

        GenerateResponse? payload;
        try
        {
            using var response = await _http.PostAsJsonAsync("/api/generate", body, SerializerOptions, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new OllamaException(
                    $"Ollama returned {(int)response.StatusCode} for model '{config.Model}': {detail}");
            }

            payload = await response.Content.ReadFromJsonAsync<GenerateResponse>(cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (OllamaException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OllamaException($"Ollama call failed for model '{config.Model}'.", ex);
        }

        if (payload is null)
        {
            throw new OllamaException($"Ollama returned an empty body for model '{config.Model}'.");
        }

        return new CompletionResult
        {
            Model = config.Model,
            Text = payload.Response ?? string.Empty,
            PromptTokens = payload.PromptEvalCount,
            CompletionTokens = payload.EvalCount,
            TotalDuration = FromNanoseconds(payload.TotalDuration),
            LoadDuration = FromNanoseconds(payload.LoadDuration),
            PromptEvalDuration = FromNanoseconds(payload.PromptEvalDuration),
            EvalDuration = FromNanoseconds(payload.EvalDuration)
        };
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var body = new EmbeddingRequest { Model = _options.EmbeddingModel, Prompt = text };

        using var response = await _http.PostAsJsonAsync("/api/embeddings", body, SerializerOptions, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct)
            .ConfigureAwait(false);
        return payload?.Embedding ?? Array.Empty<float>();
    }

    private static TimeSpan FromNanoseconds(long nanoseconds) => TimeSpan.FromTicks(nanoseconds / 100);

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    // ---- wire types -------------------------------------------------------

    private sealed record GenerateRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("system")] public string? System { get; init; }
        [JsonPropertyName("prompt")] public required string Prompt { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
        [JsonPropertyName("keep_alive")] public string? KeepAlive { get; init; }
        [JsonPropertyName("format")] public JsonNode? Format { get; init; }
        [JsonPropertyName("options")] public GenerateOptions? Options { get; init; }
    }

    private sealed record GenerateOptions
    {
        [JsonPropertyName("num_predict")] public int NumPredict { get; init; }
        [JsonPropertyName("temperature")] public double Temperature { get; init; }
        [JsonPropertyName("num_ctx")] public int NumCtx { get; init; }
    }

    private sealed record GenerateResponse
    {
        [JsonPropertyName("response")] public string? Response { get; init; }
        [JsonPropertyName("prompt_eval_count")] public int PromptEvalCount { get; init; }
        [JsonPropertyName("eval_count")] public int EvalCount { get; init; }
        [JsonPropertyName("total_duration")] public long TotalDuration { get; init; }
        [JsonPropertyName("load_duration")] public long LoadDuration { get; init; }
        [JsonPropertyName("prompt_eval_duration")] public long PromptEvalDuration { get; init; }
        [JsonPropertyName("eval_duration")] public long EvalDuration { get; init; }
    }

    private sealed record EmbeddingRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("prompt")] public required string Prompt { get; init; }
    }

    private sealed record EmbeddingResponse
    {
        [JsonPropertyName("embedding")] public float[]? Embedding { get; init; }
    }

    private sealed record TagsResponse
    {
        [JsonPropertyName("models")] public List<TagModel>? Models { get; init; }
    }

    private sealed record TagModel
    {
        [JsonPropertyName("name")] public required string Name { get; init; }
    }
}
