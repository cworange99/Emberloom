using System.Diagnostics;
using Emberloom.Mind.Ollama;

namespace Emberloom.Bench;

public sealed record CallSample
{
    public required string Scenario { get; init; }
    public required string Model { get; init; }
    public required string LaneName { get; init; }
    public required int PromptTokens { get; init; }
    public required int CompletionTokens { get; init; }
    public required double WallSeconds { get; init; }
    public required double EvalSeconds { get; init; }
    public required double LoadSeconds { get; init; }
    public required double TokensPerSecond { get; init; }
    public required bool SchemaOk { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    public string? Sample { get; init; }
    public string? Error { get; init; }
}

public sealed record ScenarioSummary
{
    public required string Scenario { get; init; }
    public required string Purpose { get; init; }
    public required string LaneName { get; init; }
    public required int Runs { get; init; }
    public required int Errors { get; init; }
    public required int SchemaFailures { get; init; }
    public required double MedianWallSeconds { get; init; }
    public required double MedianTokensPerSecond { get; init; }
    public required double MeanPromptTokens { get; init; }
    public required double MeanCompletionTokens { get; init; }

    /// <summary>
    /// Wall time adjusted for the retries a schema failure would cost in production. A model that
    /// fails one call in five really takes 1.25x as long per usable result.
    /// </summary>
    public double EffectiveWallSeconds =>
        Runs == 0 || SchemaFailures >= Runs
            ? MedianWallSeconds
            : MedianWallSeconds * Runs / (Runs - SchemaFailures);

    public string? FirstProblem { get; init; }
    public string? Excerpt { get; init; }
}

public sealed record ModelResult
{
    public required string DeepModel { get; init; }
    public required string FastModel { get; init; }
    public required IReadOnlyList<ScenarioSummary> Scenarios { get; init; }
    public required IReadOnlyList<CallSample> Samples { get; init; }
    public string? OllamaPs { get; init; }

    /// <summary>
    /// Sustained calls per minute under the design's expected call mix. This is the number §2's
    /// table is really about, and it supersedes the estimate there.
    /// </summary>
    public double SustainedCallsPerMinute
    {
        get
        {
            var weighted = 0.0;
            var totalWeight = 0.0;

            foreach (var scenario in Scenarios)
            {
                if (!CallMix.TryGetValue(scenario.Scenario, out var weight))
                {
                    continue;
                }

                weighted += scenario.EffectiveWallSeconds * weight;
                totalWeight += weight;
            }

            if (totalWeight <= 0)
            {
                return 0;
            }

            var secondsPerCall = weighted / totalWeight;
            return secondsPerCall > 0 ? 60.0 / secondsPerCall : 0;
        }
    }

    /// <summary>
    /// Expected share of each call type in a running colony. Reflection dominates because it is
    /// the core loop; kindling is rare because a mind is kindled once.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> CallMix = new Dictionary<string, double>
    {
        ["reflection"] = 0.35,
        ["dialogue"] = 0.30,
        ["idle-thought"] = 0.15,
        ["deliberation"] = 0.10,
        ["coinage"] = 0.06,
        ["kindling"] = 0.04
    };
}

public sealed class BenchRunner
{
    private readonly int _runs;
    private readonly Action<string> _log;
    private readonly Uri _endpoint;

    public BenchRunner(int runs, Action<string> log, Uri endpoint)
    {
        _runs = runs;
        _log = log;
        _endpoint = endpoint;
    }

    public async Task<ModelResult> RunAsync(
        string deepModel,
        string fastModel,
        int deepCtx,
        int fastCtx,
        CancellationToken ct = default)
    {
        var options = new OllamaOptions
        {
            Endpoint = _endpoint,
            Deep = new LaneConfig { Model = deepModel, NumCtx = deepCtx, KeepAlive = "60m" },
            Fast = new LaneConfig { Model = fastModel, NumCtx = fastCtx, KeepAlive = "60m" }
        };

        using var client = new OllamaClient(options);

        // Both lanes resident before anything is timed, with a long keep_alive — the design
        // assumes neither model ever reloads mid-session, so measuring a cold load would lie.
        _log($"  warming {deepModel} (deep) and {fastModel} (fast)...");
        await client.WarmAsync(Lane.Deep, ct).ConfigureAwait(false);
        await client.WarmAsync(Lane.Fast, ct).ConfigureAwait(false);

        var samples = new List<CallSample>();
        var summaries = new List<ScenarioSummary>();

        foreach (var scenario in BenchScenarios.All())
        {
            var laneModel = scenario.Lane == Lane.Fast ? fastModel : deepModel;
            _log($"  {scenario.Name,-14} lane={scenario.Lane.ToString().ToLowerInvariant(),-4} model={laneModel}");

            var scenarioSamples = new List<CallSample>();

            for (var run = 0; run < _runs; run++)
            {
                ct.ThrowIfCancellationRequested();
                var sample = await RunOnceAsync(client, scenario, laneModel, ct).ConfigureAwait(false);
                scenarioSamples.Add(sample);

                var status = sample.Error is not null
                    ? $"ERROR {sample.Error}"
                    : $"{sample.CompletionTokens,4} tok  {sample.WallSeconds,6:F2}s  {sample.TokensPerSecond,5:F1} tok/s  {(sample.SchemaOk ? "schema ok" : "SCHEMA FAIL")}";
                _log($"      run {run + 1}/{_runs}  {status}");
            }

            samples.AddRange(scenarioSamples);
            summaries.Add(Summarise(scenario, scenarioSamples));
        }

        return new ModelResult
        {
            DeepModel = deepModel,
            FastModel = fastModel,
            Scenarios = summaries,
            Samples = samples,
            OllamaPs = await client.ResidencyAsync(ct).ConfigureAwait(false)
        };
    }

    private static async Task<CallSample> RunOnceAsync(
        OllamaClient client,
        BenchScenario scenario,
        string laneModel,
        CancellationToken ct)
    {
        var request = new CompletionRequest
        {
            System = scenario.System,
            Prompt = scenario.Prompt,
            Schema = scenario.Schema,
            MaxTokens = scenario.MaxTokens,
            Temperature = scenario.Temperature,
            Lane = scenario.Lane
        };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await client.CompleteAsync(request, ct).ConfigureAwait(false);
            stopwatch.Stop();

            var check = SchemaCheck.Validate(result.Text, scenario.Schema);

            return new CallSample
            {
                Scenario = scenario.Name,
                Model = laneModel,
                LaneName = scenario.Lane.ToString().ToLowerInvariant(),
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                WallSeconds = stopwatch.Elapsed.TotalSeconds,
                EvalSeconds = result.EvalDuration.TotalSeconds,
                LoadSeconds = result.LoadDuration.TotalSeconds,
                TokensPerSecond = result.TokensPerSecond,
                SchemaOk = check.Ok,
                Problems = check.Problems,
                Sample = Excerpt(result.Text)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new CallSample
            {
                Scenario = scenario.Name,
                Model = laneModel,
                LaneName = scenario.Lane.ToString().ToLowerInvariant(),
                PromptTokens = 0,
                CompletionTokens = 0,
                WallSeconds = stopwatch.Elapsed.TotalSeconds,
                EvalSeconds = 0,
                LoadSeconds = 0,
                TokensPerSecond = 0,
                SchemaOk = false,
                Error = ex.Message
            };
        }
    }

    private static ScenarioSummary Summarise(BenchScenario scenario, IReadOnlyList<CallSample> samples)
    {
        var usable = samples.Where(s => s.Error is null).ToList();

        return new ScenarioSummary
        {
            Scenario = scenario.Name,
            Purpose = scenario.Purpose,
            LaneName = scenario.Lane.ToString().ToLowerInvariant(),
            Runs = samples.Count,
            Errors = samples.Count - usable.Count,
            SchemaFailures = samples.Count(s => !s.SchemaOk),
            MedianWallSeconds = Median(usable.Select(s => s.WallSeconds)),
            MedianTokensPerSecond = Median(usable.Select(s => s.TokensPerSecond)),
            MeanPromptTokens = usable.Count > 0 ? usable.Average(s => s.PromptTokens) : 0,
            MeanCompletionTokens = usable.Count > 0 ? usable.Average(s => s.CompletionTokens) : 0,
            FirstProblem = samples.FirstOrDefault(s => !s.SchemaOk)?.Problems.FirstOrDefault()
                           ?? samples.FirstOrDefault(s => s.Error is not null)?.Error,
            Excerpt = usable.FirstOrDefault(s => s.SchemaOk)?.Sample
        };
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();
        if (ordered.Count == 0)
        {
            return 0;
        }

        var mid = ordered.Count / 2;
        return ordered.Count % 2 == 1 ? ordered[mid] : (ordered[mid - 1] + ordered[mid]) / 2;
    }

    private static string Excerpt(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 400 ? collapsed : collapsed[..400] + "...";
    }
}
