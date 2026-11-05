using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;
using Runic.Platform;
using Runic.Platform.MacOS;
using Runic.Platform.Runtime;
using Runic.Platform.Windows;

namespace CsWebUiWindowProbes;

// The platforms' unowned file dialogs inside RunicCsWebUiHost.Run, on Windows and macOS (--probe-unowned-dialogs): two
// windows' dialogs each close on their own, and closing the first leaves the second. Windows shows both at once, each on
// an STA thread of its own. On macOS no AppKit event loop runs with browser windows, so each panel runs modally (runModal)
// on the main thread the host serves, one at a time: the second request waits until the first panel has closed, cleanup
// included, and then shows; a waiting request gives up at once when cancelled. Under an AppKit event loop macOS shows
// both at once, which the native AppKit tests check. Cancellation closes them; nobody answers a dialog. Not part of the
// package smoke, which builds Program.cs alone.
internal static class UnownedDialogProbe
{
    private static readonly TimeSpan Shown = TimeSpan.FromSeconds(2), Closed = TimeSpan.FromSeconds(30), Waiting = TimeSpan.FromSeconds(5);
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
            // The first dialog closes while the second is shown, or on macOS waits for it: they do not nest, and the first
            // completes without waiting for the second.
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
                await EndedAsync(one, "the first dialog, while the second was requested");
                Require(!two.IsCompleted, $"Closing the first dialog ended the second: {Describe(two)}.");
                // On macOS the second panel shows now, and its modal loop runs until it is cancelled.
                await Task.Delay(Shown);
                Require(!two.IsCompleted, $"The second dialog ended before it was cancelled: {Describe(two)}.");
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
                // On macOS it waits for the third panel and gives up at once; on Windows it is shown and closes.
                await EndedAsync(two, "a dialog requested while another was shown", OperatingSystem.IsMacOS() ? Waiting : Closed);
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
    private static async Task EndedAsync(Task<PickerResult<IDirectoryLease>> pick, string name, TimeSpan? within = null)
    {
        var limit = within ?? Closed;
        try
        {
            var result = await pick.WaitAsync(limit);
            Step($"{name} ended: {result}");
            Require(result is PickerResult<IDirectoryLease>.Dismissed, $"Cancelling {name} reported {result}.");
        }
        catch (OperationCanceledException) { Step($"{name} ended: cancelled"); }
        catch (TimeoutException)
        {
            // Reported here too: a panel that did not close keeps the main thread in its modal loop, so Run cannot return.
            var message = $"Cancelling {name} did not close it within {limit.TotalSeconds} s.";
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
