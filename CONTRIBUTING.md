# Contributing to Meridian

Thanks for helping. Issues, ideas and pull requests are all welcome — for anything bigger than a fix,
open an issue first so we can agree the shape before you spend time on it.

## Build and test

You need the **.NET 10 SDK**. No database server: tests, samples and the dashboard use in-memory data
and embedded DuckDB.

```bash
dotnet build               # everything: libraries, hosts, samples, benchmarks
dotnet test                # the full suite
dotnet run --project src/Meridian.Hosts.Http             # dashboard → http://localhost:5731
dotnet run --project samples/Meridian.Sample.QuickStart  # the README quick start
```

Other samples: `samples/Meridian.Sample` (offline), `samples/Meridian.Sample.Weather` (the Open-Meteo
public API; needs internet), `samples/Meridian.Bench.Source` (cold vs warm timing for any source,
including a MySQL database via `--mysql "<connection string>"`). Benchmarks are described in
[docs/BENCHMARKS.md](docs/BENCHMARKS.md).

## Repository layout

```
src/        the packages (see docs/ARCHITECTURE.md) and the sample REST host
tests/      one test project per package
samples/    runnable examples
bench/      BenchmarkDotNet micro-benchmarks, the scale harness, and committed results
docs/       time model, architecture, benchmarks, usage
```

## The rules

- **Warnings are errors**, nullable is on. The build must stay clean.
- **Every change comes with tests.** Tests run on Linux and Windows in CI.
- **Time and pushdown changes need parity tests.** Anything touching time zones, bucketing or a source's
  pushdown must show the result equals the in-engine one across zones and DST changes — see the existing
  parity tests in `tests/Meridian.Sources.DuckDb.Tests`. A good check: break your change on purpose and
  confirm a test fails.
- **Keep presentation out of data.** Points carry values and time only; colours, labels and formats are
  applied at projection (see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)).
- **Match the surrounding code** — naming, comment density, idioms.

## Pull requests

`main` is protected: changes go through pull requests and both CI jobs must pass. Keep PRs focused, fill
in the template, and add a line to `CHANGELOG.md` under an *Unreleased* heading for anything user-facing.
PRs are squash-merged, so the PR title becomes the commit message — make it a clear summary.

## Releases

Maintainers release by pushing a tag like `v0.1.0-preview.3`; the release workflow builds, tests and
publishes every package to NuGet with that version, and creates a GitHub release.

## Conduct and security

Everyone taking part is expected to follow the [code of conduct](CODE_OF_CONDUCT.md). Please report
security issues privately as described in [SECURITY.md](SECURITY.md), not in public issues.
