using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using TUnit.Core;
using static HostedApplicationTests;

// Elevated-peer admission (host-protocol.md, Elevated-peer admission; D059.13): every listener of a guest of the
// elevated pair, its channel listener and its Desktop host's page and frame listeners, admits only loopback peers that
// run elevated, checked before the request is read; a refused peer is closed without a response and logged (event 2029,
// elevated-peer-refused). The in-process cases replace the guest's check of its peers (RunicGuestOptions.CheckPeer), as
// HostedElevationTests replaces its check of its parent; the process cases use the real check.
internal sealed class HostedElevatedPeerTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    [Test, NotInParallel(Order = 46)]
    public async Task AnElevatedPairChecksThePeerOfEveryListenerBeforeReadingItsRequest()
    {
        var admit = false;
        var asked = 0;
        await using var started = await StartGuestAsync(new GuestPrivilegeFacts(Elevated: true, SameUser: true, ParentElevated: true), options => options with
        {
            CheckPeer = (_, _) =>
            {
                Interlocked.Increment(ref asked);
                return Volatile.Read(ref admit);
            },
            CanCheckPeers = true,
        });
        Check(started.Desktop.PeerAdmission is not null, "The elevated guest's Desktop host admits every peer.");

        // The channel listener: a refused peer gets no response, not even the upgrade's refusal.
        Check(await StatusLineAsync(started.Channel, "/_runic_host") is null, "The channel listener answered a refused peer.");
        Check(Volatile.Read(ref asked) >= 1, "The channel listener did not check its peer.");
        Check(await TestTimeouts.UntilAsync(() => started.Logs.Entries.Any(static entry => entry.EventId == 2029 &&
            entry.Message.Contains("elevated-peer-refused", StringComparison.Ordinal)), StepTimeout), "Event 2029 was not logged.");
        Volatile.Write(ref admit, true);
        Check(await StatusLineAsync(started.Channel, "/_runic_host") is { } answered && answered.StartsWith("HTTP/1.1 ", StringComparison.Ordinal),
            "The channel listener did not answer an admitted peer.");

        // The page listener of the guest's Desktop host.
        await using var surface = await started.Desktop.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<p>page</p>") });
        Volatile.Write(ref admit, false);
        Check(await StatusLineAsync(surface.Url, surface.Url.PathAndQuery) is null, "The page listener answered a refused peer.");
        Volatile.Write(ref admit, true);
        Check(await StatusLineAsync(surface.Url, surface.Url.PathAndQuery) is not null, "The page listener did not answer an admitted peer.");
    }

    // A guest that does not run elevated checks no peer; an elevated guest without a peer lookup (macOS) keeps its
    // listeners' other protections and checks none either.
    [Test, NotInParallel(Order = 46)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task OnlyAnElevatedGuestWithAPeerLookupChecksPeers(bool elevated, bool canCheck)
    {
        var asked = 0;
        await using var started = await StartGuestAsync(new GuestPrivilegeFacts(elevated, SameUser: true, ParentElevated: elevated), options => options with
        {
            CheckPeer = (_, _) =>
            {
                Interlocked.Increment(ref asked);
                return false;
            },
            CanCheckPeers = canCheck,
        });
        Check(started.Desktop.PeerAdmission is null, "The guest's Desktop host checks its peers.");
        Check(await StatusLineAsync(started.Channel, "/_runic_host") is not null, "The channel listener refused a peer.");
        Check(Volatile.Read(ref asked) == 0, "A peer was checked.");
    }

    // On a system without a peer lookup an elevated guest refuses region mode: whoever renders the region's page could
    // not be checked (host-protocol.md, Elevated-peer admission, macOS).
    [Test, NotInParallel(Order = 74)]
    public async Task AnElevatedGuestWithoutAPeerLookupRefusesRegions()
    {
        await using var host = await RunicTestHost.StartGuestAsync(Process(HostedGuestProgram.Guest("elevated-unchecked")), new RunicTestHostOptions
        {
            StartTimeout = TestTimeouts.Scale(TimeSpan.FromSeconds(30)),
            Features = ["menus/1", "dialogs/1", "window-services/1", "region/1"],
            EmbedWindow = static _ => true,
            AllowElevatedGuest = true,
        });
        var window = await host.WaitForWindowAsync(nameof(ConformanceMainWindow), TestTimeouts.Scale(TimeSpan.FromSeconds(30)));
        using var cancel = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
        try
        {
            _ = await window.AttachRegionAsync("http://127.0.0.1:38200", allowSameSite: true, cancellationToken: cancel.Token);
            throw new InvalidOperationException("The elevated guest without a peer lookup attached a region.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("rejected refused", StringComparison.Ordinal)) { }
        Check(!window.IsClosed && !window.IsAttached, "The window closed or attached.");
    }

    // The real check: a guest that takes itself for one of the elevated pair (HostedGuestProgram "elevated-peers") refuses
    // this test process, which does not run elevated, on its channel listener, so the host cannot connect.
    [Test, NotInParallel(Order = 46)]
    public async Task AnElevatedGuestRefusesAHostProcessThatDoesNotRunElevated()
    {
        if (Environment.IsPrivilegedProcess) Skip.Test("This test process runs elevated, so the guest admits it.");
        if (!Runic.Desktop.Internal.ElevatedPeers.IsAvailable) Skip.Test("Runic has no peer lookup on this system.");
        var start = Stopwatch.StartNew();
        try
        {
            await using var host = await RunicTestHost.StartGuestAsync(Process(HostedGuestProgram.Guest("elevated-peers")), new RunicTestHostOptions
            {
                StartTimeout = TestTimeouts.Scale(TimeSpan.FromSeconds(5)),
                AllowElevatedGuest = true,
            });
            throw new InvalidOperationException("A host process that does not run elevated connected to the elevated guest.");
        }
        catch (RunicHostedApplicationException error) when (error.Code is "guest-start-timeout" or "handshake-timeout" or "handshake-failed" or "guest-exited")
        {
            Check(start.Elapsed < TestTimeouts.Scale(TimeSpan.FromSeconds(60)), "The start did not end in time.");
        }
    }

    private static ProcessStartInfo Process(RunicGuestStartInfo start)
    {
        var info = new ProcessStartInfo(start.FileName) { WorkingDirectory = start.WorkingDirectory };
        foreach (var argument in start.Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    // The guest in this process, against an offer that allows an elevated guest, up to its channel line; disposing it
    // closes standard input, so the start ends with host-gone.
    private static async Task<StartedGuest> StartGuestAsync(GuestPrivilegeFacts facts, Func<RunicGuestOptions, RunicGuestOptions> configure)
    {
        var input = new AnonymousPipeServerStream(PipeDirection.Out);
        var output = new GuestOutput();
        var logs = new RecordingLoggerFactory();
        var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WaitForConnection = false, Security = DesktopSecurityPolicy.Default });
        var guest = configure(RunicGuestOptions.FromStreams(new AnonymousPipeClientStream(PipeDirection.In, input.ClientSafePipeHandle), output) with
        {
            AllowElevatedHost = true,
            ReadPrivileges = () => facts,
            StartTimeout = TestTimeouts.Scale(StepTimeout * 3),
            ExitProcess = static _ => { },
        });
        var offer = HostProtocol.Line(HostProtocol.OfferPrefix,
            HostProtocol.Offer([HostProtocol.Version], HostProtocol.Base64Url(RandomNumberGenerator.GetBytes(32)), "Peer host", "1.0", elevatedGuest: true));
        input.Write(Encoding.UTF8.GetBytes(offer));
        input.Flush();
        var start = HostedGuest.StartAsync(guest, desktop, logs.CreateLogger("Runic.Application.Views.Desktop"));
        var line = await output.ChannelLineAsync(TestTimeouts.Scale(StepTimeout));
        var url = HostProtocol.ParseLine(line, HostProtocol.ChannelPrefix)?["url"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"The guest wrote '{line}'.");
        return new StartedGuest(input, desktop, logs, start, new Uri(url));
    }

    private sealed record StartedGuest(AnonymousPipeServerStream Input, DesktopHost Desktop, RecordingLoggerFactory Logs, Task<HostedGuest> Start,
        Uri Channel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Input.DisposeAsync();
            try { await (await Start.WaitScaledAsync(StepTimeout)).DisposeAsync(); }
            catch (RunicHostedApplicationException) { }
            await Desktop.DisposeAsync();
        }
    }

    // The status line a raw GET of path receives, or null when the listener closed the connection without one.
    private static async Task<string?> StatusLineAsync(Uri listener, string path)
    {
        using var client = new TcpClient(listener.HostNameType == UriHostNameType.IPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Parse(listener.DnsSafeHost), listener.Port);
        var stream = client.GetStream();
        using var timeout = TestTimeouts.Cancellation(StepTimeout);
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {listener.Authority}\r\nConnection: close\r\n\r\n"), timeout.Token);
            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0) return null;
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            var end = text.IndexOf('\r');
            return end < 0 ? text : text[..end];
        }
        catch (IOException) { return null; }
    }

    // The guest's standard output: its channel line.
    private sealed class GuestOutput : TextWriter
    {
        private readonly StringBuilder _line = new();
        private readonly TaskCompletionSource<string> _channel = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_line)
            {
                if (value != '\n')
                {
                    _line.Append(value);
                    return;
                }
                var line = _line.ToString().TrimEnd('\r');
                _line.Clear();
                if (line.StartsWith(HostProtocol.ChannelPrefix, StringComparison.Ordinal) || line.StartsWith(HostProtocol.RefusedPrefix, StringComparison.Ordinal))
                    _channel.TrySetResult(line);
            }
        }

        public Task<string> ChannelLineAsync(TimeSpan timeout) => _channel.Task.WaitAsync(timeout);
    }
}
