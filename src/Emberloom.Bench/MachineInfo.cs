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

    public string? ProfileOverride { get; init; }

    /// <summary>
    /// Which of the design's two hardware profiles this machine is. Decided by whether a
    /// discrete GPU with meaningful VRAM is present, since that is what the 5x gap comes from.
    /// </summary>
    public string Profile => ProfileOverride
                             ?? (Gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                                 || Gpu.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)
                                 ? "target"
                                 : "modest");
}

/// <summary>
/// Overrides for the machine being measured. Needed because the benchmark can drive an Ollama
/// on another box over the network, in which case the process's own CPU and GPU describe the
/// wrong computer entirely — and a report labelled with the wrong hardware is worse than none.
/// </summary>
public sealed record MachineOverride
{
    public string? Cpu { get; init; }
    public string? Gpu { get; init; }
    public double? TotalRamGb { get; init; }
    public string? Os { get; init; }
    public int? LogicalCores { get; init; }
    public string? Profile { get; init; }

    public bool Any => Cpu is not null || Gpu is not null || TotalRamGb is not null
                       || Os is not null || LogicalCores is not null || Profile is not null;
}

public static class MachineInfo
{
    public static async Task<MachineDescription> DescribeAsync(
        MachineOverride? overrides = null,
        CancellationToken ct = default)
    {
        // Only probe the local machine when nothing has been supplied for that field — a remote
        // run should never silently report the laptop's CPU next to the GPU's throughput.
        var remote = overrides?.Any == true;

        return new MachineDescription
        {
            Os = overrides?.Os ?? RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            LogicalCores = overrides?.LogicalCores ?? Environment.ProcessorCount,
            Cpu = overrides?.Cpu ?? (remote ? "unknown" : await QueryAsync("cpu", ct).ConfigureAwait(false)),
            Gpu = overrides?.Gpu ?? (remote ? "unknown" : await QueryAsync("gpu", ct).ConfigureAwait(false)),
            TotalRamGb = overrides?.TotalRamGb
                         ?? Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024, 1),
            OllamaVersion = (await RunAsync("ollama", "--version", ct).ConfigureAwait(false)).Trim(),
            ProfileOverride = overrides?.Profile
        };
    }

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
