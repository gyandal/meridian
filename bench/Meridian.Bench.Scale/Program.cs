using System.Globalization;
using System.Text.Json;
using Meridian.Bench.Scale;

// Scale benchmark: generate a synthetic longitudinal dataset as Parquet, then time Meridian over it.
//
//   dotnet run -c Release --project bench/Meridian.Bench.Scale -- generate --entities 2000 --metrics 5 --days 730 --per-day 24
//   dotnet run -c Release --project bench/Meridian.Bench.Scale -- run --label my-laptop
//   dotnet run -c Release --project bench/Meridian.Bench.Scale -- all --entities 200 --days 365   # both, small
//
// Rows = entities × metrics × days × per-day × (1 − gap%). Results land in bench/results/*.json, which the
// dashboard (src/Meridian.Hosts.Http) charts under "Benchmarks".

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        usage: <generate|run|all> [options]

        generate   --entities 1000  --metrics 5  --days 730  --per-day 24  --gap 2  --data data/bench
        run        --data data/bench  --report-entities 25  --report-days 365  --iterations 5
                   --warm-iterations 50  --label local  --out bench/results
        """);
    return 0;
}

var opts = ParseOptions(args.Skip(1));
string dataDir = Str("data", "data/bench");

if (args[0] is "generate" or "all")
{
    var spec = new DatasetSpec(Int("entities", 1000), Int("metrics", 5), Int("days", 730), Int("per-day", 24), Int("gap", 2));
    Console.WriteLine($"Generating ~{spec.Slots * (100 - spec.GapPercent) / 100:N0} rows → {Path.GetFullPath(dataDir)}");
    var manifest = Generator.Generate(spec, dataDir);
    Console.WriteLine($"  {manifest.Rows:N0} rows · {manifest.Bytes / 1024d / 1024:N0} MB Parquet · {manifest.GenerateSeconds:N1} s · DuckDB {manifest.DuckDbVersion}");
    Console.WriteLine();
}

if (args[0] is "run" or "all")
{
    var run = await Runner.RunAsync(new RunOptions(
        dataDir,
        Int("report-entities", 25),
        Int("report-days", 365),
        Int("iterations", 5),
        Int("warm-iterations", 50),
        Str("label", "local")));

    var outDir = Str("out", "bench/results");
    Directory.CreateDirectory(outDir);
    var path = Path.Combine(outDir, $"{run.RunId}.json");
    File.WriteAllText(path, JsonSerializer.Serialize(run, Json.Options));
    Console.WriteLine();
    Console.WriteLine($"Results → {Path.GetFullPath(path)}");
}

return 0;

int Int(string name, int fallback) =>
    opts.TryGetValue(name, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

string Str(string name, string fallback) => opts.TryGetValue(name, out var v) ? v : fallback;

static Dictionary<string, string> ParseOptions(IEnumerable<string> rest)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? pending = null;
    foreach (var arg in rest)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal)) pending = arg[2..];
        else if (pending is not null) { map[pending] = arg; pending = null; }
    }
    return map;
}
