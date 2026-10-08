using System.Diagnostics;
using System.Windows;
using Runic.Navigation.Examples.Notes.Tests;

// The scenarios run as async code on the WPF dispatcher of one Application, like the app itself:
// awaits resume on the UI thread, and the dispatcher keeps pumping in between.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var failures = new List<string>();
        app.DispatcherUnhandledException += (_, e) =>
        {
            failures.Add($"dispatcher: {e.Exception}");
            e.Handled = true;
        };
        app.Startup += async (_, _) =>
        {
            foreach (var (name, test) in ScenarioTests.All)
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    await test().WaitAsync(Scene.Timeout * 2);
                    Console.WriteLine($"PASS {name} ({clock.ElapsedMilliseconds} ms)");
                }
                catch (Exception error)
                {
                    failures.Add($"{name}: {error}");
                    Console.WriteLine($"FAIL {name} ({clock.ElapsedMilliseconds} ms): {error}");
                }
            }
            app.Shutdown();
        };
        app.Run();
        if (failures.Count == 0)
        {
            Console.WriteLine("Notes navigation scenarios passed.");
            return 0;
        }
        Console.Error.WriteLine($"{failures.Count} failure(s):{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        return 1;
    }
}
