using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emberloom.Bench;

public sealed record BenchReport
{
    public required DateTimeOffset RunAt { get; init; }
    public required MachineDescription Machine { get; init; }
    public required int RunsPerScenario { get; init; }
    public required IReadOnlyList<ModelResult> Models { get; init; }
}

public static class Report
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<(string RawPath, string MarkdownPath)> WriteAsync(
        BenchReport report,
        string outputDirectory,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var stamp = report.RunAt.ToString("yyyy-MM-dd-HHmm");
        var slug = $"m0-{report.Machine.Profile}-{stamp}";

        var rawPath = Path.Combine(outputDirectory, $"{slug}.raw.json");
        await File.WriteAllTextAsync(rawPath, JsonSerializer.Serialize(report, Json), ct).ConfigureAwait(false);

        var markdownPath = Path.Combine(outputDirectory, $"{slug}.md");
        await File.WriteAllTextAsync(markdownPath, Markdown(report), ct).ConfigureAwait(false);

        return (rawPath, markdownPath);
    }

    private static string Markdown(BenchReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# M0 benchmark — {report.Machine.Profile} profile");
        sb.AppendLine();
        sb.AppendLine($"Run {report.RunAt:yyyy-MM-dd HH:mm zzz} · {report.RunsPerScenario} runs per scenario.");
        sb.AppendLine();
        sb.AppendLine("These measurements supersede every throughput estimate in the design's §2, §5 and §6.");
        sb.AppendLine();

        sb.AppendLine("## Machine");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| CPU | {report.Machine.Cpu} ({report.Machine.LogicalCores} logical cores) |");
        sb.AppendLine($"| GPU | {report.Machine.Gpu} |");
        sb.AppendLine($"| RAM | {report.Machine.TotalRamGb} GB |");
        sb.AppendLine($"| OS | {report.Machine.Os} ({report.Machine.Architecture}) |");
        sb.AppendLine($"| Ollama | {report.Machine.OllamaVersion} |");
        sb.AppendLine($"| Profile | **{report.Machine.Profile}** |");
        sb.AppendLine();

        sb.AppendLine("## Headline — sustained calls per minute");
        sb.AppendLine();
        sb.AppendLine("Under the expected call mix (reflection 35%, dialogue 30%, idle 15%, deliberation 10%, coinage 6%, kindling 4%), adjusted for schema retries.");
        sb.AppendLine();
        sb.AppendLine("| Deep lane | Fast lane | Sustained calls/min | Schema failures |");
        sb.AppendLine("|---|---|---:|---:|");
        foreach (var model in report.Models)
        {
            var failures = model.Scenarios.Sum(s => s.SchemaFailures);
            var total = model.Scenarios.Sum(s => s.Runs);
            sb.AppendLine($"| `{model.DeepModel}` | `{model.FastModel}` | **{model.SustainedCallsPerMinute:F1}** | {failures}/{total} |");
        }

        sb.AppendLine();

        foreach (var model in report.Models)
        {
            sb.AppendLine($"## `{model.DeepModel}` (deep) + `{model.FastModel}` (fast)");
            sb.AppendLine();
            sb.AppendLine("| Scenario | Lane | Median wall | Median tok/s | Prompt tok | Out tok | Schema fails | Effective wall |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");

            foreach (var scenario in model.Scenarios)
            {
                var fails = scenario.Errors > 0
                    ? $"{scenario.SchemaFailures}/{scenario.Runs} ({scenario.Errors} err)"
                    : $"{scenario.SchemaFailures}/{scenario.Runs}";

                sb.AppendLine(
                    $"| {scenario.Scenario} | {scenario.LaneName} | {scenario.MedianWallSeconds:F2}s | " +
                    $"{scenario.MedianTokensPerSecond:F1} | {scenario.MeanPromptTokens:F0} | " +
                    $"{scenario.MeanCompletionTokens:F0} | {fails} | {scenario.EffectiveWallSeconds:F2}s |");
            }

            sb.AppendLine();

            var problems = model.Scenarios.Where(s => s.FirstProblem is not null).ToList();
            if (problems.Count > 0)
            {
                sb.AppendLine("**Failures**");
                sb.AppendLine();
                foreach (var scenario in problems)
                {
                    sb.AppendLine($"- `{scenario.Scenario}`: {scenario.FirstProblem}");
                }

                sb.AppendLine();
            }

            sb.AppendLine("<details><summary>Sample output per scenario</summary>");
            sb.AppendLine();
            foreach (var scenario in model.Scenarios.Where(s => s.Excerpt is not null))
            {
                sb.AppendLine($"**{scenario.Scenario}** — {scenario.Purpose}");
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(scenario.Excerpt);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            sb.AppendLine("</details>");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(model.OllamaPs))
            {
                sb.AppendLine("<details><summary>Residency (<code>ollama ps</code>) with both lanes warm</summary>");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(model.OllamaPs.TrimEnd());
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
