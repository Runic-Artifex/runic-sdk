using System.Diagnostics;
using System.Net;
using Runic.Application.Testing;
using Runic.Application.Views;
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

    // The host goes away: its standard input ends and the channel closes, so the guest closes its window and exits.
    [Test]
    public async Task ExitsWhenTheHostGoesAway()
    {
        if (Elevated)
        {
            await RefusedAsElevatedAsync();
            return;
        }
        var host = await RunicTestHost.StartGuestAsync(Guest(), new RunicTestHostOptions { StartTimeout = TestTimeouts.Scale(Timeout) });
        _ = await host.WaitForWindowAsync(nameof(LoggingWindow), TestTimeouts.Scale(Timeout));
        var exited = host.Exited;
        var watch = Stopwatch.StartNew();
        await host.DisposeAsync();
        Require(exited.IsCompletedSuccessfully && exited.Result == 0, $"The guest did not exit by itself: {(exited.IsCompleted ? exited.Result : "still running")}.", host);
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
