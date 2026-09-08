using Emberloom.Bench;
using Emberloom.Mind.Calls;
using Emberloom.Mind.Ollama;

// M0 — benchmark this machine before designing further.
//
// Measures the five real call types plus idle thought at their real token caps, with both lanes
// resident and a long keep_alive, and records the schema-adherence failure rate per model.
// Every throughput figure in the design is sized from what this produces.

var options = CliOptions.Parse(args);
if (options is null)
{
    CliOptions.PrintUsage();
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var probeOptions = new OllamaOptions
{
    Endpoint = options.Endpoint,
    Deep = new LaneConfig { Model = options.DeepModels[0] },
    Fast = new LaneConfig { Model = options.FastModel }
};

using (var probe = new OllamaClient(probeOptions))
{
    if (!await probe.IsAvailableAsync(cts.Token))
    {
        Console.Error.WriteLine($"Ollama is not reachable at {probeOptions.Endpoint}. Start it and try again.");
        return 2;
    }

    var installed = await probe.ListModelsAsync(cts.Token);
    var wanted = options.DeepModels.Append(options.FastModel).Distinct();
    var missing = wanted.Where(m => !installed.Any(i => i == m || i == m + ":latest")).ToList();

    if (missing.Count > 0)
    {
        Console.Error.WriteLine($"Not pulled: {string.Join(", ", missing)}");
        Console.Error.WriteLine($"Run: {string.Join(" && ", missing.Select(m => $"ollama pull {m}"))}");
        return 3;
    }
}

var machine = await MachineInfo.DescribeAsync(options.Machine, cts.Token);

Console.WriteLine($"M0 benchmark — {machine.Profile} profile");
Console.WriteLine($"  {machine.Cpu}");
Console.WriteLine($"  {machine.Gpu}");
Console.WriteLine($"  {machine.TotalRamGb} GB RAM · Ollama {machine.OllamaVersion}");
Console.WriteLine($"  {options.Runs} runs per scenario, fast lane = {options.FastModel}");
Console.WriteLine($"  endpoint {options.Endpoint}");
Console.WriteLine();

var runner = new BenchRunner(options.Runs, Console.WriteLine, options.Endpoint);
var results = new List<ModelResult>();

foreach (var deepModel in options.DeepModels)
{
    Console.WriteLine($"[{deepModel}]");
    try
    {
        results.Add(await runner.RunAsync(
            deepModel,
            options.FastModel,
            options.DeepContext,
            options.FastContext,
            cts.Token));
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
        break;
    }
    catch (Exception ex)
    {
        // One model failing to load must not throw away the models that already ran. On the
        // modest profile this happens for real: Ollama returns 500 trying to hold a third model
        // resident alongside two others, which is itself an M0 finding worth recording.
        Console.Error.WriteLine($"  {deepModel} failed: {ex.Message}");
        Console.Error.WriteLine("  Skipping it and keeping the results collected so far.");
    }

    Console.WriteLine();
}

if (results.Count == 0)
{
    Console.Error.WriteLine("No results to write.");
    return 4;
}

var report = new BenchReport
{
    RunAt = DateTimeOffset.Now,
    Machine = machine,
    RunsPerScenario = options.Runs,
    Models = results
};

var (rawPath, markdownPath) = await Report.WriteAsync(report, options.OutputDirectory, CancellationToken.None);

Console.WriteLine("Sustained calls/min under the expected call mix:");
foreach (var result in results)
{
    Console.WriteLine($"  {result.DeepModel,-18} {result.SustainedCallsPerMinute,6:F1}");
}

Console.WriteLine();
Console.WriteLine($"Report:  {markdownPath}");
Console.WriteLine($"Raw:     {rawPath}");
return 0;

internal sealed record CliOptions
{
    public required IReadOnlyList<string> DeepModels { get; init; }
    public required string FastModel { get; init; }
    public required int Runs { get; init; }
    public required int DeepContext { get; init; }
    public required int FastContext { get; init; }
    public required string OutputDirectory { get; init; }
    public required Uri Endpoint { get; init; }
    public required MachineOverride Machine { get; init; }

    public static CliOptions? Parse(string[] args)
    {
        var deep = new List<string>();
        var fast = "llama3.2:1b";
        var runs = 3;
        var deepContext = 4096;
        var fastContext = 2048;
        var output = Path.Combine("bench", "results");
        var endpoint = new Uri("http://localhost:11434");
        string? cpu = null, gpu = null, os = null, profile = null;
        double? ram = null;
        int? cores = null;

        for (var i = 0; i < args.Length; i++)
        {
            var next = i + 1 < args.Length ? args[i + 1] : null;

            switch (args[i])
            {
                case "--models" or "-m" when next is not null:
                    deep.AddRange(next.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    i++;
                    break;
                case "--fast" or "-f" when next is not null:
                    fast = next;
                    i++;
                    break;
                case "--runs" or "-r" when next is not null && int.TryParse(next, out var parsedRuns):
                    runs = parsedRuns;
                    i++;
                    break;
                case "--deep-ctx" when next is not null && int.TryParse(next, out var parsedDeepCtx):
                    deepContext = parsedDeepCtx;
                    i++;
                    break;
                case "--fast-ctx" when next is not null && int.TryParse(next, out var parsedFastCtx):
                    fastContext = parsedFastCtx;
                    i++;
                    break;
                case "--out" or "-o" when next is not null:
                    output = next;
                    i++;
                    break;
                case "--cap" when next is not null:
                    // Sweeps a per-call-type token ceiling without editing CallBudgets, so a cap
                    // can be justified by measurement rather than guessed at.
                    var parts = next.Split('=', 2);
                    if (parts.Length != 2
                        || !CallBudgets.TryParse(parts[0], out var call)
                        || !int.TryParse(parts[1], out var cap))
                    {
                        Console.Error.WriteLine($"Bad --cap '{next}'. Expected e.g. --cap reflection=448");
                        return null;
                    }

                    CallBudgets.Override(call, cap);
                    i++;
                    break;
                case "--endpoint" or "-e" when next is not null:
                    endpoint = new Uri(next);
                    i++;
                    break;
                case "--cpu" when next is not null:
                    cpu = next; i++; break;
                case "--gpu" when next is not null:
                    gpu = next; i++; break;
                case "--os" when next is not null:
                    os = next; i++; break;
                case "--ram" when next is not null && double.TryParse(next, out var parsedRam):
                    ram = parsedRam; i++; break;
                case "--cores" when next is not null && int.TryParse(next, out var parsedCores):
                    cores = parsedCores; i++; break;
                case "--profile" when next is not null:
                    profile = next; i++; break;
                case "--help" or "-h":
                    return null;
                default:
                    Console.Error.WriteLine($"Unrecognised argument: {args[i]}");
                    return null;
            }
        }

        if (deep.Count == 0)
        {
            deep.AddRange(["llama3.2:3b", "qwen2.5:3b"]);
        }

        return new CliOptions
        {
            DeepModels = deep,
            FastModel = fast,
            Runs = runs,
            DeepContext = deepContext,
            FastContext = fastContext,
            OutputDirectory = output,
            Endpoint = endpoint,
            Machine = new MachineOverride
            {
                Cpu = cpu,
                Gpu = gpu,
                Os = os,
                TotalRamGb = ram,
                LogicalCores = cores,
                Profile = profile
            }
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            M0 benchmark — sizes every throughput figure in the design.

              --models, -m   Comma-separated deep-lane models   (default: llama3.2:3b,qwen2.5:3b)
              --fast,   -f   Fast-lane model                    (default: llama3.2:1b)
              --runs,   -r   Runs per scenario                   (default: 3)
              --deep-ctx     Deep lane context window            (default: 4096)
              --fast-ctx     Fast lane context window            (default: 2048)
              --out,    -o   Output directory                    (default: bench/results)
              --cap          Override one call cap, e.g. --cap reflection=448 (repeatable)

            Target profile example:
              dotnet run --project src/Emberloom.Bench -- -m qwen3:14b,qwen2.5:14b -f llama3.2:3b -r 5
            """);
    }
}
