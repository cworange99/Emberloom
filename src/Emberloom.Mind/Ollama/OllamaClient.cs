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
            Think = config.Think,
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
        // A request timeout surfaces as TaskCanceledException, which derives from
        // OperationCanceledException — so "not a cancellation" is not enough to tell a timeout
        // apart from the caller giving up. Only a cancellation the caller actually asked for is
        // allowed through; everything else is an ordinary failed call, which is what lets the
        // layer above fall back rather than fault.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
            RequestSystem = request.System ?? string.Empty,
            RequestPrompt = request.Prompt,
            ThinkingText = payload.Thinking,
            PromptTokens = payload.PromptEvalCount,
            CompletionTokens = payload.EvalCount,
            TotalDuration = FromNanoseconds(payload.TotalDuration),
            LoadDuration = FromNanoseconds(payload.LoadDuration),
            PromptEvalDuration = FromNanoseconds(payload.PromptEvalDuration),
            EvalDuration = FromNanoseconds(payload.EvalDuration)
        };
    }

    /// <summary>
    /// Which models are resident right now, and how much of each sits in VRAM versus system RAM.
    /// This is the VRAM-headroom figure M0 is meant to record at Target, and it has to come from
    /// the API rather than <c>ollama ps</c> so that it describes whichever machine is actually
    /// serving — which is not necessarily the one running this process.
    /// </summary>
    public async Task<string> ResidencyAsync(CancellationToken ct = default)
    {
        try
        {
            var payload = await _http.GetFromJsonAsync<RunningResponse>("/api/ps", ct).ConfigureAwait(false);
            if (payload?.Models is not { Count: > 0 })
            {
                return "nothing resident";
            }

            var lines = payload.Models.Select(m =>
            {
                var total = m.Size;
                var vram = m.SizeVram;
                var share = total > 0 ? (double)vram / total : 0;
                var where = share >= 0.99 ? "100% GPU" : share <= 0.01 ? "100% CPU" : $"{share:P0} GPU";

                return $"{m.Name,-24} {Gib(total),8} resident  {Gib(vram),8} in VRAM  {where}";
            });

            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"residency unavailable: {ex.Message}";
        }
    }

    private static string Gib(long bytes) => $"{bytes / 1024.0 / 1024 / 1024:F2} GiB";

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
        [JsonPropertyName("think")] public bool? Think { get; init; }
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
        [JsonPropertyName("thinking")] public string? Thinking { get; init; }
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

    private sealed record RunningResponse
    {
        [JsonPropertyName("models")] public List<RunningModel>? Models { get; init; }
    }

    private sealed record RunningModel
    {
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("size")] public long Size { get; init; }
        [JsonPropertyName("size_vram")] public long SizeVram { get; init; }
    }
}
