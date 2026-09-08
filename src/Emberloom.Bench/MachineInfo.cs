using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Emberloom.Bench;

public sealed record MachineDescription
{
    public required string Os { get; init; }
    public required string Architecture { get; init; }
    public required int LogicalCores { get; init; }
    public required string Cpu { get; init; }
    public required string Gpu { get; init; }
    public required double TotalRamGb { get; init; }
    public required string OllamaVersion { get; init; }

    /// <summary>
    /// Which of the design's two hardware profiles this machine is. Decided by whether a
    /// discrete GPU with meaningful VRAM is present, since that is what the 5x gap comes from.
    /// </summary>
    public string Profile => Gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                             || Gpu.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)
        ? "target"
        : "modest";
}

public static class MachineInfo
{
    public static async Task<MachineDescription> DescribeAsync(CancellationToken ct = default)
    {
        return new MachineDescription
        {
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            LogicalCores = Environment.ProcessorCount,
            Cpu = await QueryAsync("cpu", ct).ConfigureAwait(false),
            Gpu = await QueryAsync("gpu", ct).ConfigureAwait(false),
            TotalRamGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024, 1),
            OllamaVersion = (await RunAsync("ollama", "--version", ct).ConfigureAwait(false)).Trim()
        };
    }

    /// <summary>
    /// <c>ollama ps</c> reports the CPU/GPU split and resident size per model — the VRAM
    /// headroom figure M0 is meant to record on the Target profile.
    /// </summary>
    public static Task<string> OllamaPsAsync(CancellationToken ct = default) =>
        RunAsync("ollama", "ps", ct);

    private static async Task<string> QueryAsync(string what, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "unknown";
        }

        var script = what == "cpu"
            ? "(Get-CimInstance Win32_Processor).Name"
            : "(Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name) -join ', '";

        var output = await RunAsync("powershell", $"-NoProfile -Command \"{script}\"", ct).ConfigureAwait(false);
        var trimmed = output.Trim();
        return string.IsNullOrEmpty(trimmed) ? "unknown" : trimmed;
    }

    private static async Task<string> RunAsync(string file, string arguments, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return output;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
