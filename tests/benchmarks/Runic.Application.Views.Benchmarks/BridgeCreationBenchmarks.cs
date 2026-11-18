using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Runic.Application.Views.Generated.Bridges;

namespace Runic.Application.Views.Benchmarks;

/// <summary>
/// Creating and disposing a generated Bridge of a realistic ViewModel (BridgeCreationModels.cs) once its type's
/// descriptors exist: the cost of each further Bridge of the type. <see cref="BridgeCreation.RunCold"/> measures the first
/// Bridge of each type instead.
/// </summary>
[MemoryDiagnoser]
public class BridgeCreationBenchmarks
{
    private HostTransport _host = null!;
    private CreationVm0 _model = null!;

    [GlobalSetup]
    public void Setup()
    {
        _host = new HostTransport();
        _model = new CreationVm0();
        new CreationVm0Bridge(_host, _model).Dispose();
    }

    [GlobalCleanup]
    public void Cleanup() => _host.Dispose();

    [Benchmark]
    public void CreateAndDispose() => new CreationVm0Bridge(_host, _model).Dispose();
}

/// <summary>
/// The first Bridge of each of the ten ViewModel types, in a fresh process: its type's descriptors are created then (a
/// generated constructor, or one read of its table), and in a JIT run the Bridge's code is compiled. Run it in several
/// processes and compare medians: <c>dotnet run -c Release --project tests/benchmarks/Runic.Application.Views.Benchmarks -- bridge-creation</c>.
/// </summary>
public static class BridgeCreation
{
    public static void RunCold()
    {
        using var host = new HostTransport();
        // The runtime's own first-use costs, outside the measured types.
        new BoardBridge(host, new BoardModel(rows: 1), content: null).Dispose();
        Func<IDisposable>[] creators =
        [
            () => new CreationVm0Bridge(host, new CreationVm0()), () => new CreationVm1Bridge(host, new CreationVm1()),
            () => new CreationVm2Bridge(host, new CreationVm2()), () => new CreationVm3Bridge(host, new CreationVm3()),
            () => new CreationVm4Bridge(host, new CreationVm4()), () => new CreationVm5Bridge(host, new CreationVm5()),
            () => new CreationVm6Bridge(host, new CreationVm6()), () => new CreationVm7Bridge(host, new CreationVm7()),
            () => new CreationVm8Bridge(host, new CreationVm8()), () => new CreationVm9Bridge(host, new CreationVm9()),
        ];
        var cold = new double[creators.Length];
        var warm = new double[creators.Length];
        for (var index = 0; index < creators.Length; index++)
        {
            var watch = Stopwatch.StartNew();
            creators[index]().Dispose();
            cold[index] = watch.Elapsed.TotalMicroseconds;
            watch.Restart();
            creators[index]().Dispose();
            warm[index] = watch.Elapsed.TotalMicroseconds;
        }
        Array.Sort(cold);
        Array.Sort(warm);
        Console.WriteLine($"first Bridge of a type: median {cold[cold.Length / 2]:F1} µs (min {cold[0]:F1}, max {cold[^1]:F1}); second: median {warm[warm.Length / 2]:F1} µs");
    }
}
