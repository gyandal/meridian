using BenchmarkDotNet.Running;
using Meridian.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(Workloads).Assembly).Run(args);
