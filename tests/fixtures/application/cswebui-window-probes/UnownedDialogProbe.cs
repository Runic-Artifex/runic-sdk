using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;
using Runic.Platform;
using Runic.Platform.MacOS;
using Runic.Platform.Runtime;
using Runic.Platform.Windows;

namespace CsWebUiWindowProbes;

// The platforms' unowned file dialogs inside RunicCsWebUiHost.Run, on Windows and macOS (--probe-unowned-dialogs): two
// windows' dialogs shown at once each close on their own, whichever closes first. On macOS no AppKit event loop runs, so
// each panel runs modally (runModal) on the main thread the host serves, and a second request waits for the first.
// Cancellation closes them; nobody answers a dialog. Not part of the package smoke, which builds Program.cs alone.
internal static class UnownedDialogProbe
{
    private static readonly TimeSpan Shown = TimeSpan.FromSeconds(2), Closed = TimeSpan.FromSeconds(30);
    private static readonly System.Diagnostics.Stopwatch Started = System.Diagnostics.Stopwatch.StartNew();

    internal static int Run()
    {
        IPickerBackend? dialogs = OperatingSystem.IsWindowsVersionAtLeast(8) ? WindowsPlatformProvider.CreateUnownedFileDialogs()
            : OperatingSystem.IsMacOS() ? MacOSPlatformProvider.CreateUnownedFileDialogs() : null;
        if (dialogs is null)
        {
            Console.WriteLine("CSWEBUI_UNOWNED_DIALOGS_SKIPPED: Windows and macOS only");
            return 0;
        }
        // A dialog that never closes keeps the process: fail instead of waiting for the job's timeout.
        new Thread(static () =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(2));
            Console.Error.WriteLine("The unowned dialog probe did not finish within 2 minutes.");
            Environment.Exit(3);
        })
        { IsBackground = true }.Start();
        Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", null);
        var services = new ServiceCollection();
        services.AddRunicViews();
        services.AddRunicPlatformServices(dialogs);
        using var provider = services.BuildServiceProvider();
        var result = RunicCsWebUiHost.Run(provider, async _ =>
        {
            // The first dialog closes while the second is still shown: they do not nest.
            using (var first = new CancellationTokenSource())
            using (var second = new CancellationTokenSource())
            {
                var one = Open(dialogs, "first", first.Token);
                await Task.Delay(Shown);
                var two = Open(dialogs, "second", second.Token);
                await Task.Delay(Shown);
                Require(!one.IsCompleted && !two.IsCompleted, $"A dialog ended before it was cancelled: {Describe(one)}, {Describe(two)}.");
                Step("cancelling the first dialog");
                first.Cancel();
                await EndedAsync(one, "the first dialog, while the second was shown");
                Require(!two.IsCompleted, $"Closing the first dialog ended the second: {Describe(two)}.");
                await Task.Delay(Shown);
                Step("cancelling the second dialog");
                second.Cancel();
                await EndedAsync(two, "the second dialog");
            }
            // A request that waits for another dialog, as on macOS, gives up at once when cancelled.
            using (var first = new CancellationTokenSource())
            using (var second = new CancellationTokenSource())
            {
                var one = Open(dialogs, "third", first.Token);
                await Task.Delay(Shown);
                var two = Open(dialogs, "fourth", second.Token);
                await Task.Delay(TimeSpan.FromSeconds(1));
                Step("cancelling the fourth dialog");
                second.Cancel();
                await EndedAsync(two, "a dialog requested while another was shown");
                Require(!one.IsCompleted, $"Cancelling the later dialog ended the earlier one: {Describe(one)}.");
                Step("cancelling the third dialog");
                first.Cancel();
                await EndedAsync(one, "the earlier dialog");
            }
            return 0;
        });
        Require(result == 0, $"Run returned {result}.");
        Console.WriteLine("CSWEBUI_UNOWNED_DIALOGS_OK");
        return 0;
    }

    // Progress on standard error, so that a probe the watchdog ends shows where it stopped.
    private static void Step(string step) => Console.Error.WriteLine($"{Started.ElapsedMilliseconds} ms: {step}");

    private static Task<PickerResult<IDirectoryLease>> Open(IPickerBackend dialogs, string name, CancellationToken cancellationToken)
    {
        Step($"opening the {name} dialog");
        return dialogs.OpenDirectoryAsync(new OpenDirectoryOptions(OwnerPolicy.PreferOwner) { Title = $"Runic CS-WebUI probe: {name} dialog" }, cancellationToken).AsTask();
    }

    // A cancelled dialog throws OperationCanceledException; one closed otherwise reports Dismissed.
    private static async Task EndedAsync(Task<PickerResult<IDirectoryLease>> pick, string name)
    {
        try
        {
            var result = await pick.WaitAsync(Closed);
            Step($"{name} ended: {result}");
            Require(result is PickerResult<IDirectoryLease>.Dismissed, $"Cancelling {name} reported {result}.");
        }
        catch (OperationCanceledException) { Step($"{name} ended: cancelled"); }
        catch (TimeoutException)
        {
            // Reported here too: a panel that did not close keeps the main thread in its modal loop, so Run cannot return.
            var message = $"Cancelling {name} did not close it within {Closed.TotalSeconds} s.";
            Step(message);
            throw new InvalidOperationException(message);
        }
    }

    private static string Describe(Task task) => task.Status.ToString();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
