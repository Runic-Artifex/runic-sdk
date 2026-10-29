using System.Diagnostics;
using System.Globalization;
using Runic.Navigation;

namespace Runic.Application.Views.Benchmarks;

// Measurements that are rates rather than durations, printed as a table:
//   dotnet run -c Release --project tests/benchmarks/Runic.Application.Views.Benchmarks -- probe
//
// - notifications: a model posts about 10,000 counter updates a second for 2 s; how many frames reach the page.
// - model turns: 64 routes on one RunicModelContext publish 200 changes each; the peak number of thread-pool threads.
public static class Probe
{
    public static void Run()
    {
        Notifications();
        ModelTurns();
    }

    private static void Notifications()
    {
        using var page = new CountingTransport();
        var model = new ProducerModel(items: 0, archive: 0);
        using var bridge = new ProducerBridge(page, model);
        _ = page.Request("{\"kind\":\"snapshot\",\"route\":\"producer\"}");
        var duration = TimeSpan.FromSeconds(2);
        var interval = Stopwatch.Frequency / 10_000;
        var started = Stopwatch.GetTimestamp();
        var before = page.Pushes;
        var updates = 0;
        var next = started;
        while (Stopwatch.GetElapsedTime(started) < duration)
        {
            while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(20);
            next += interval;
            model.Counter++;
            updates++;
        }
        var last = model.Counter.ToString(CultureInfo.InvariantCulture);
        var delivered = page.Expect($"\"value\":{last}}}");
        if (!delivered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The last update was not published.");
        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var frames = page.Pushes - before;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"notifications: {updates / duration.TotalSeconds:F0} updates/s -> {frames / elapsed:F0} frames/s ({frames} frames, last update delivered)"));
    }

    private static void ModelTurns()
    {
        const int routes = 64, changes = 200;
        var context = new RunicModelContext();
        using var page = new CountingTransport();
        var models = Enumerable.Range(0, routes).Select(_ => new ProducerModel(items: 0, archive: 0)).ToArray();
        using var ownership = RunicModelContextRegistry.Shared.Bind(context, [.. models]);
        var bridges = new ProducerBridge[routes];
        for (var index = 0; index < routes; index++)
        {
            bridges[index] = new ProducerBridge(page, models[index], $"route{index.ToString(CultureInfo.InvariantCulture)}");
            _ = page.Request($"{{\"kind\":\"snapshot\",\"route\":\"route{index.ToString(CultureInfo.InvariantCulture)}\"}}");
        }
        var threadsBefore = ThreadPool.ThreadCount;
        var peak = threadsBefore;
        using var sampling = new CancellationTokenSource();
        var sampler = new Thread(() =>
        {
            while (!sampling.IsCancellationRequested)
            {
                peak = Math.Max(peak, ThreadPool.ThreadCount);
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        sampler.Start();
        var started = Stopwatch.GetTimestamp();
        for (var change = 1; change <= changes; change++)
        {
            var value = change;
            context.InvokeAsync(() => { foreach (var model in models) model.Counter = value; }).AsTask().GetAwaiter().GetResult();
        }
        // Every route publishes its last value; reading each route's state waits for its drain.
        while (true)
        {
            var settled = Enumerable.Range(0, routes).All(index =>
                page.Request($"{{\"kind\":\"snapshot\",\"route\":\"route{index.ToString(CultureInfo.InvariantCulture)}\"}}")
                    .Contains($"\"counter\":{changes.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal));
            if (settled) break;
            Thread.Sleep(1);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        sampling.Cancel();
        sampler.Join();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"model turns: {routes} routes x {changes} changes in {elapsed.TotalMilliseconds:F0} ms, {page.Pushes} pushes, thread-pool threads {threadsBefore} -> peak {peak}"));
        foreach (var bridge in bridges) bridge.Dispose();
        context.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
