using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// Runs in-process so the benchmark uses this checkout's locked SDK and
// package cache without generating and restoring a second project.
// Usage: dotnet run -c Release --project tests/benchmarks/Runic.Application.Views.Benchmarks -- --filter '*'
var config = DefaultConfig.Instance
    .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance))
    .AddDiagnoser(MemoryDiagnoser.Default);
BenchmarkSwitcher.FromAssembly(typeof(Runic.Application.Views.Benchmarks.SnapshotBenchmarks).Assembly).Run(args, config);
