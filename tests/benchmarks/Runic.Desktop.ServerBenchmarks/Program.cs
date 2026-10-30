using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// Runs in-process so the benchmark uses this checkout's locked SDK and package cache.
// Usage: eng/native/build-runic-server.sh && dotnet run -c Release --project tests/benchmarks/Runic.Desktop.ServerBenchmarks -- --filter '*'
var config = DefaultConfig.Instance
    .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance))
    .AddDiagnoser(MemoryDiagnoser.Default);
BenchmarkSwitcher.FromAssembly(typeof(Runic.Desktop.ServerBenchmarks.SurfaceTransportBenchmarks).Assembly).Run(args, config);
