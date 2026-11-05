using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// appearance/1: the appearance feed watches IDesktopSettings while it has open windows and sets each window's
// WindowContentSession.Appearance; a read that fails keeps the last appearance, a watch that throws is logged and the
// next window watches again, and a source the feed created is disposed after the last window closed. Only windows that
// nothing but this computer can reach get it (DesktopWindowLocality).
internal sealed class AppearanceFeedChecks
{
    [Test]
    public async Task OpenWindowsFollowTheDesktopsSettingsWhileAnyIsOpen()
    {
        var created = new List<FakeSettings>();
        var feed = new DesktopAppearanceFeed(settings: null, () => { var settings = new FakeSettings(); created.Add(settings); return settings; });
        using var firstTransport = new InMemoryBridgeTransport();
        using var first = new WindowContentSession(firstTransport, new WindowContentSessionOptions());
        using var secondTransport = new InMemoryBridgeTransport();
        using var second = new WindowContentSession(secondTransport, new WindowContentSessionOptions());

        var firstRegistration = feed.Add(first);
        var settings = created.Single();
        var dark = new DesktopAppearance(DesktopColorScheme.Dark) { Contrast = DesktopContrast.More };
        await settings.EmitAsync(new PlatformResult<DesktopAppearance>.Success(dark));
        await WaitAsync(() => first.Appearance == dark, "the first window's appearance");

        // A window that opens later gets the last appearance at once.
        var secondRegistration = feed.Add(second);
        Require(second.Appearance == dark, $"The second window opened with {second.Appearance}.");
        Require(settings.Watches == 1, $"{settings.Watches} watches for two windows.");

        // A read that fails, transiently or not, keeps the last appearance.
        await settings.EmitAsync(new PlatformResult<DesktopAppearance>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        await settings.EmitAsync(new PlatformResult<DesktopAppearance>.Failed(PlatformFailureCode.IOError));
        await WaitAsync(() => settings.Pending == 0, "the failed reads");
        await Task.Delay(50);
        Require(first.Appearance == dark && second.Appearance == dark, $"A failed read changed the appearance to {first.Appearance}.");
        var light = new DesktopAppearance(DesktopColorScheme.Light) { TextScale = 1.5 };
        await settings.EmitAsync(new PlatformResult<DesktopAppearance>.Success(light));
        await WaitAsync(() => first.Appearance == light && second.Appearance == light, "the appearance after failed reads");

        // The last window that closes stops the watch and disposes the source the feed created; the next window starts
        // another.
        firstRegistration.Dispose();
        firstRegistration.Dispose();
        Require(!settings.Cancelled && !settings.Disposed, "The watch stopped while a window was open.");
        secondRegistration.Dispose();
        await WaitAsync(() => settings.Cancelled && settings.Disposed, "the watch to stop and its source to be disposed");
        using var thirdTransport = new InMemoryBridgeTransport();
        using var third = new WindowContentSession(thirdTransport, new WindowContentSessionOptions());
        using var thirdRegistration = feed.Add(third);
        Require(third.Appearance is null, $"A window after the last closed started with {third.Appearance}.");
        await created[^1].EmitAsync(new PlatformResult<DesktopAppearance>.Success(dark));
        await WaitAsync(() => third.Appearance == dark, "the third window's appearance");
        Require(created.Count == 2 && created[1].Watches == 1, $"{created.Count} settings for two watches.");
        Require(first.Appearance == light, "A closed window kept following.");
    }

    [Test]
    public async Task AWatchThatFailsIsLoggedAndTheNextWindowWatchesAgain()
    {
        // The application's IDesktopSettings: the feed uses it, and never disposes it.
        var settings = new FakeSettings();
        var feed = new DesktopAppearanceFeed(settings);
        var logger = new RecordingLogger();
        using var firstTransport = new InMemoryBridgeTransport();
        using var first = new WindowContentSession(firstTransport, new WindowContentSessionOptions());
        using var firstRegistration = feed.Add(first, logger);
        var dark = new DesktopAppearance(DesktopColorScheme.Dark);
        await settings.EmitAsync(new PlatformResult<DesktopAppearance>.Success(dark));
        await WaitAsync(() => first.Appearance == dark, "the first appearance");

        // A container disposed its settings: the watch throws, which the window's logger reports.
        settings.Fail(new ObjectDisposedException(nameof(FakeSettings)));
        await WaitAsync(() => logger.Events.Contains(2009), "event 2009");
        Require(first.Appearance == dark, "A failed watch cleared the appearance.");

        // The next window that opens watches again, and both follow it.
        using var secondTransport = new InMemoryBridgeTransport();
        using var second = new WindowContentSession(secondTransport, new WindowContentSessionOptions());
        using var secondRegistration = feed.Add(second, logger);
        Require(second.Appearance == dark, "The next window did not get the last appearance.");
        await WaitAsync(() => settings.Watches == 2, "a second watch");
        var light = new DesktopAppearance(DesktopColorScheme.Light);
        await settings.EmitAsync(new PlatformResult<DesktopAppearance>.Success(light));
        await WaitAsync(() => first.Appearance == light && second.Appearance == light, "both windows to follow the new watch");
        Require(!settings.Disposed, "The feed disposed the application's settings.");
    }

    [Test]
    public void OnlyWindowsThatOnlyThisComputerReachesAreLocal()
    {
        var loopback = new Uri("http://127.0.0.1:5000/");
        Require(DesktopWindowLocality.IsLocal(false, DesktopNetworkExposure.Loopback, () => loopback, _ => true), "A loopback window was not local.");
        Require(DesktopWindowLocality.IsLocal(false, DesktopNetworkExposure.Loopback, () => loopback, _ => null),
            "A loopback host whose listeners cannot be read was not local.");
        Require(!DesktopWindowLocality.IsLocal(true, DesktopNetworkExposure.Loopback, () => loopback, _ => true), "A serve-only window was local.");
        Require(!DesktopWindowLocality.IsLocal(false, DesktopNetworkExposure.AllInterfaces, () => loopback, _ => true),
            "A host on every interface was local.");
        Require(!DesktopWindowLocality.IsLocal(false, DesktopNetworkExposure.Loopback, () => loopback, _ => false),
            "A window whose port listens beyond loopback was local.");
        Require(!DesktopWindowLocality.IsLocal(false, DesktopNetworkExposure.Loopback, () => new Uri("http://192.0.2.1:5000/"), _ => true),
            "A window on another address was local.");
    }

    [Test]
    public void ListenersOnLoopbackAndOnEveryInterfaceAreToldApart()
    {
        var local = new TcpListener(IPAddress.Loopback, 0);
        var shared = new TcpListener(IPAddress.Any, 0);
        local.Start();
        shared.Start();
        try
        {
            Require(DesktopWindowLocality.ListensOnLoopbackOnly(((IPEndPoint)local.LocalEndpoint).Port) is true or null, "A loopback listener was not local.");
            Require(DesktopWindowLocality.ListensOnLoopbackOnly(((IPEndPoint)shared.LocalEndpoint).Port) is false or null,
                "A listener on every interface was local.");
            Require(DesktopWindowLocality.ListensOnLoopbackOnly(0) == false && DesktopWindowLocality.ListensOnLoopbackOnly(70_000) == false,
                "An invalid port was local.");
        }
        finally
        {
            local.Stop();
            shared.Stop();
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
        Require(condition(), $"Timed out waiting for {what}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<int> _events = [];
        public IReadOnlyList<int> Events { get { lock (_events) return [.. _events]; } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_events) _events.Add(eventId.Id);
        }
    }

    // Each watch reads the values emitted while it runs, until it is cancelled or Fail throws in it.
    private sealed class FakeSettings : IDesktopSettings
    {
        private Channel<PlatformResult<DesktopAppearance>> _channel = Channel.CreateUnbounded<PlatformResult<DesktopAppearance>>();
        private Exception? _failure;

        public int Watches { get; private set; }
        public bool Cancelled { get; private set; }
        public bool Disposed { get; private set; }
        public int Pending => _channel.Reader.Count;

        public ValueTask EmitAsync(PlatformResult<DesktopAppearance> value) => _channel.Writer.WriteAsync(value);

        public void Fail(Exception failure)
        {
            _failure = failure;
            _channel.Writer.TryComplete();
        }

        public ValueTask<PlatformResult<DesktopAppearance>> ReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlatformResult<DesktopAppearance>>(new PlatformResult<DesktopAppearance>.Success(new DesktopAppearance()));

        public async IAsyncEnumerable<PlatformResult<DesktopAppearance>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Watches++;
            Cancelled = false;
            var channel = _channel;
            while (true)
            {
                PlatformResult<DesktopAppearance> value;
                try { value = await channel.Reader.ReadAsync(cancellationToken); }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    _channel = Channel.CreateUnbounded<PlatformResult<DesktopAppearance>>();
                    yield break;
                }
                catch (ChannelClosedException) when (_failure is { } failure)
                {
                    _failure = null;
                    _channel = Channel.CreateUnbounded<PlatformResult<DesktopAppearance>>();
                    throw failure;
                }
                yield return value;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
