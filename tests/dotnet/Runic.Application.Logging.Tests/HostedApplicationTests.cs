using System.Diagnostics;
using System.Net;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Application.Logging.Tests;

// RunicTestHost against this suite's application in hosted mode (runic-host/1): a real guest process, its pipes, its
// isolated channel listener and its surface. Each case starts its own process, so the cases may run in parallel.
internal sealed class HostedApplicationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static ProcessStartInfo Guest() => LoggingApplication.SelfStartInfo(HostedApplication.Argument);

    // Hosted mode refuses an elevated guest, as a CI container running as root would be.
    private static bool Elevated => Environment.IsPrivilegedProcess;

    [Test]
    public async Task HostsTheWindowAndClosesItWhenTheGuestAllows()
    {
        if (Elevated)
        {
            await RefusedAsElevatedAsync();
            return;
        }
        await using var host = await RunicTestHost.StartGuestAsync(Guest(), new RunicTestHostOptions { StartTimeout = TestTimeouts.Scale(Timeout) });
        Require(host.Guest.ApplicationId == "dev.runic.tests.logging" && host.Guest.Name.Length > 0, $"The guest named itself {host.Guest}.", host);

        var window = await host.WaitForWindowAsync(nameof(LoggingWindow), TestTimeouts.Scale(Timeout));
        Require(window.Type == typeof(LoggingWindow).FullName && window.GuardsClose && !window.Refused && window.OwnerId is null,
            $"The window-open did not describe the guarded LoggingWindow: {window.Type}, guard {window.GuardsClose}.", host);
        Require(window.LaunchUrl.IsLoopback && window.LaunchUrl.AbsolutePath.Contains("/_runic_launch/", StringComparison.Ordinal),
            $"The window's address is not a one-time launch address: {window.LaunchUrl}.", host);

        // The launch address binds the first client with its cookie and leads it to the page, as the host's window.
        using (var handler = new HttpClientHandler { CookieContainer = new CookieContainer() })
        using (var client = new HttpClient(handler))
        {
            var page = await client.GetStringAsync(window.LaunchUrl);
            Require(page.Contains("Served without a window", StringComparison.Ordinal), $"The launch address did not lead to the page: {page}", host);
        }
        // It is one-time: another client gets nothing.
        using (var other = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
        {
            using var reused = await other.GetAsync(window.LaunchUrl);
            Require(reused.StatusCode == HttpStatusCode.Forbidden, $"A second client used the launch address: {reused.StatusCode}.", host);
        }

        Require(await window.RequestCloseAsync(), "The guest refused the close.", host);
        await window.Closed.WaitScaledAsync(Timeout);
        Require(await host.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly after its window closed.", host);
    }

    // menus/1 and dialogs/1 under RunicTestHost: a guest tests its menu bar, its commands and its dialogs without a native
    // host.
    [Test]
    public async Task TestsTheGuestsMenusAndDialogs()
    {
        if (Elevated)
        {
            await RefusedAsElevatedAsync();
            return;
        }
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "rows");
            // The first round answers as the user would, with the second filter; the second as a host that refuses the file
            // dialog, fails the box and lost the window; the third with a relative path, which the test learns of.
            var round = 0;
            await using var host = await RunicTestHost.StartGuestAsync(LoggingApplication.SelfStartInfo(HostedApplication.MenusArgument), new RunicTestHostOptions
            {
                StartTimeout = TestTimeouts.Scale(Timeout),
                FileDialog = (request, _) => Task.FromResult(Interlocked.Increment(ref round) switch
                {
                    1 => RunicTestDialogAnswer.Select([file], filter: 1),
                    2 => RunicTestDialogAnswer.Refuse("Not in this test."),
                    _ => RunicTestDialogAnswer.Select(["notes.txt"]),
                }),
                MessageBox = (request, _) => Task.FromResult(Volatile.Read(ref round) == 1
                    ? RunicTestDialogAnswer.Choose("undo")
                    : RunicTestDialogAnswer.Fail(PlatformFailureCode.IOError)),
                ContextMenu = (request, _) => Task.FromResult(Volatile.Read(ref round) == 1
                    ? RunicTestDialogAnswer.Choose(RunicTestHostedWindow.FindMenuItem(request.Message.GetProperty("menu"), "Copy"))
                    : RunicTestDialogAnswer.Unavailable(PlatformUnavailableReason.OwnerClosed)),
            });
            Require(host.Features.SequenceEqual(["menus/1", "dialogs/1"]), $"The channel uses {string.Join(", ", host.Features)}.", host);
            var window = await host.WaitForWindowAsync(nameof(LoggingWindow), TestTimeouts.Scale(Timeout));
            var bar = await window.WaitForMenuBarAsync(bar => window.FindMenuItem("File", "Open…") is not null, TestTimeouts.Scale(Timeout));
            Require(bar[0].GetProperty("role").GetString() == "file" && bar[0].GetProperty("items")[1].GetProperty("enabled").GetBoolean() == false,
                $"The bar is not the guest's, with Save unavailable: {bar.GetRawText()}", host);
            Require(bar[0].GetProperty("items")[0].GetProperty("shortcut").GetString() == "Primary+O", "Open… lost its shortcut.", host);

            // The user chooses File ▸ Open…: the guest runs its command, Save becomes available, and the command shows a
            // file dialog, a message box and a context menu, which the test host answers.
            await window.InvokeMenuItemAsync(window.FindMenuItem("File", "Open…")!);
            await window.WaitForMenuBarAsync(bar => bar[0].GetProperty("items")[1].GetProperty("enabled").GetBoolean(), TestTimeouts.Scale(Timeout));
            Require(await TestTimeouts.UntilAsync(() => host.StandardError.Contains("CONTEXT edit.copy", StringComparison.Ordinal), Timeout),
                "The guest did not finish its dialogs.", host);
            Require(host.StandardError.Contains("RAN file.open", StringComparison.Ordinal) &&
                host.StandardError.Contains("OPEN " + Path.GetFileName(file) + " rows Data", StringComparison.Ordinal) &&
                host.StandardError.Contains("BOX undo", StringComparison.Ordinal), "The guest's command or dialogs did not report.", host);
            Require(window.DialogRequests.Select(static request => request.DialogKind)
                    .SequenceEqual([RunicTestDialogKind.OpenFile, RunicTestDialogKind.MessageBox, RunicTestDialogKind.ContextMenu]) &&
                window.DialogRequests[0] is { Title: "Import", FilterNames: ["Text", "Data"], SuggestedName: null } &&
                window.DialogRequests[1] is { Text: "Keep the import?", ButtonIds: ["keep", "undo"] }, "The test host did not record the dialogs.", host);

            // A refusal, a failure and a closed owner reach the guest as its platform's outcomes.
            await window.InvokeMenuItemAsync(window.FindMenuItem("File", "Open…")!);
            Require(await TestTimeouts.UntilAsync(() => host.StandardError.Contains("CONTEXT unavailable OwnerClosed", StringComparison.Ordinal), Timeout),
                "The guest did not finish its second round.", host);
            Require(host.StandardError.Contains("OPEN failed PermissionDenied", StringComparison.Ordinal) &&
                host.StandardError.Contains("BOX failed IOError", StringComparison.Ordinal), "The refusal or the failure did not reach the guest.", host);

            // A relative path is the test's mistake: the guest's dialog fails, and the test host's next wait says why.
            await window.InvokeMenuItemAsync(window.FindMenuItem("File", "Open…")!);
            Require(await TestTimeouts.UntilAsync(() => host.StandardError.Split('\n').Count(static line => line.StartsWith("CONTEXT ", StringComparison.Ordinal)) == 3, Timeout),
                "The guest did not finish its third round.", host);
            try
            {
                _ = await window.WaitForMenuBarAsync(static _ => true, TestTimeouts.Scale(Timeout));
                Require(false, "The test host did not report the relative path.", host);
            }
            catch (InvalidOperationException error) when (error.Message.Contains("notes.txt", StringComparison.Ordinal) && error.Message.Contains("FileDialog", StringComparison.Ordinal)) { }

            Require(await window.RequestCloseAsync(), "The guest refused the close.", host);
            Require(await host.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly.", host);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task StopsWithShutdown()
    {
        if (Elevated)
        {
            await RefusedAsElevatedAsync();
            return;
        }
        await using var host = await RunicTestHost.StartGuestAsync(Guest(), new RunicTestHostOptions { StartTimeout = TestTimeouts.Scale(Timeout) });
        var window = await host.WaitForWindowAsync(nameof(LoggingWindow), TestTimeouts.Scale(Timeout));
        Require(await host.StopAsync(TestTimeouts.Scale(Timeout)) == 0, "The guest did not exit cleanly after shutdown.", host);
        Require(window.IsClosed, "The guest exited without window-closed.", host);
    }

    // The host goes away: its standard input ends and the channel closes, so the guest closes its window and exits. It
    // goes away right after it answered the window-open, so the guest's open either completed or fails with
    // hosted-host-gone; either way the guest exits with 0, never with an unhandled exception.
    [Test]
    public async Task ExitsWhenTheHostGoesAway()
    {
        if (Elevated)
        {
            await RefusedAsElevatedAsync();
            return;
        }
        var host = await RunicTestHost.StartGuestAsync(Guest(), new RunicTestHostOptions { StartTimeout = TestTimeouts.Scale(Timeout) });
        var window = await host.WaitForWindowAsync(nameof(LoggingWindow), TestTimeouts.Scale(Timeout));
        Require(window.Opened.IsCompletedSuccessfully, "A listed window's open was not answered.", host);
        var exited = host.Exited;
        var watch = Stopwatch.StartNew();
        await host.DisposeAsync();
        Require(exited.IsCompletedSuccessfully && exited.Result == 0, $"The guest did not exit by itself: {(exited.IsCompleted ? exited.Result : "still running")}.", host);
        Require(!host.StandardError.Contains("Unhandled exception", StringComparison.Ordinal), "The guest crashed.", host);
        Require(watch.Elapsed < TimeSpan.FromSeconds(10), $"The guest took {watch.Elapsed} to exit.", host);
    }

    [Test]
    public async Task RefusesAnotherProtocolVersion()
    {
        var failure = await StartFailureAsync(Guest(), new RunicTestHostOptions { Protocols = [2], StartTimeout = TestTimeouts.Scale(Timeout) });
        if (Elevated) return;
        if (failure.Code != "protocol-unsupported" || failure.GuestCode != "protocol-unsupported")
            throw new InvalidOperationException($"The start failed with {failure.Code}/{failure.GuestCode}: {failure.Message}");
    }

    // A process on the channel's port that does not know the nonce, as one that took a gone guest's port would.
    [Test]
    public async Task RejectsAGuestWithoutTheNonce()
    {
        var failure = await StartFailureAsync(LoggingApplication.SelfStartInfo(FakeGuest.Argument),
            new RunicTestHostOptions { StartTimeout = TestTimeouts.Scale(Timeout) });
        if (failure.Code != "guest-not-hostable")
            throw new InvalidOperationException($"The start failed with {failure.Code}: {failure.Message}");
    }

    // A proven failed hello reply is read: protocol-mismatch ends the start as protocol-unsupported.
    [Test]
    public async Task ReportsAProvenProtocolMismatch()
    {
        var failure = await StartFailureAsync(LoggingApplication.SelfStartInfo(FakeGuest.MismatchArgument),
            new RunicTestHostOptions { StartTimeout = TestTimeouts.Scale(Timeout) });
        if (failure.Code != "protocol-unsupported")
            throw new InvalidOperationException($"The start failed with {failure.Code}: {failure.Message}");
    }

    [Test]
    public async Task ReportsAGuestThatExits()
    {
        // The test application lists its cases and exits without reading the offer.
        var failure = await StartFailureAsync(LoggingApplication.SelfStartInfo("--list-tests"),
            new RunicTestHostOptions { StartTimeout = TestTimeouts.Scale(Timeout) });
        if (failure.Code != "guest-exited") throw new InvalidOperationException($"The start failed with {failure.Code}: {failure.Message}");
    }

    [Test]
    public async Task KillsAGuestThatWritesNoChannelLine()
    {
        // The child-process mode sleeps without reading standard input.
        var failure = await StartFailureAsync(LoggingApplication.SelfStartInfo(LoggingApplication.ChildProcessArgument),
            new RunicTestHostOptions { StartTimeout = TimeSpan.FromSeconds(2) });
        if (failure.Code != "guest-start-timeout") throw new InvalidOperationException($"The start failed with {failure.Code}: {failure.Message}");
    }

    private static async Task<RunicHostedApplicationException> StartFailureAsync(ProcessStartInfo start, RunicTestHostOptions options)
    {
        try
        {
            await using var host = await RunicTestHost.StartGuestAsync(start, options);
        }
        catch (RunicHostedApplicationException error)
        {
            if (Elevated && error.GuestCode is not ("guest-elevated" or null)) throw;
            return error;
        }
        throw new InvalidOperationException("The guest was hosted.");
    }

    private static async Task RefusedAsElevatedAsync()
    {
        var failure = await StartFailureAsync(Guest(), new RunicTestHostOptions { StartTimeout = TestTimeouts.Scale(Timeout) });
        if (failure.GuestCode != "guest-elevated") throw new InvalidOperationException($"An elevated guest started: {failure.Message}");
    }

    private static void Require(bool condition, string message, RunicTestHost host)
    {
        if (!condition) throw new InvalidOperationException($"{message}\nStandard output:\n{host.StandardOutput}\nStandard error:\n{host.StandardError}");
    }
}
