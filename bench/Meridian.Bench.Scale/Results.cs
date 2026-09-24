using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meridian.Bench.Scale;

/// <summary>One benchmark run — the file format the dashboard's Benchmarks panel reads.</summary>
public sealed record BenchRun(
    int SchemaVersion,
    string RunId,
    string Label,
    DateTime TimestampUtc,
    string? Commit,
    MachineInfo Machine,
    DatasetManifest Dataset,
    ReportShape Report,
    IReadOnlyList<ScenarioResult> Scenarios);

public sealed record MachineInfo(string Os, string Cpu, int LogicalCores, double MemoryGb, string Dotnet, string DuckDb);

/// <summary>The report every scenario runs, so the numbers are comparable.</summary>
public sealed record ReportShape(string Metric, int Entities, int Days, string Transform, long RawRowsInScope);

public sealed record ScenarioResult(
    string Id,
    string Name,
    string Description,
    int Iterations,
    double MinMs,
    double MedianMs,
    double P95Ms,
    double MeanMs,
    int PointsReturned);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public static class Machine
{
    public static MachineInfo Describe(string duckDbVersion) => new(
        RuntimeInformation.OSDescription,
        CpuName(),
        Environment.ProcessorCount,
        Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024, 1),
        RuntimeInformation.FrameworkDescription,
        duckDbVersion);

    public static string? Commit()
    {
        var sha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrEmpty(sha)) return sha[..Math.Min(7, sha.Length)];
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (git is null) return null;
            var output = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit();
            return git.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git not installed
        }
    }

    private static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
                if (line is not null) return line[(line.IndexOf(':') + 1)..].Trim();
            }
            if (OperatingSystem.IsWindows())
            {
                var name = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
            if (OperatingSystem.IsMacOS())
            {
                using var p = Process.Start(new ProcessStartInfo("sysctl", "-n machdep.cpu.brand_string")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                });
                var s = p?.StandardOutput.ReadToEnd().Trim();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return RuntimeInformation.ProcessArchitecture.ToString();
    }
}
