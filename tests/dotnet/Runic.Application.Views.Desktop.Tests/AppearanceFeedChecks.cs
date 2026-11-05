using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Platform;
using TUnit.Core;

// appearance/1: RunicDesktopHost's appearance feed watches IDesktopSettings while the host has open windows and sets
// each window's WindowContentSession.Appearance; a failed read makes it unknown.
internal sealed class AppearanceFeedChecks
{
    [Test]
    public async Task OpenWindowsFollowTheDesktopsSettingsWhileAnyIsOpen()
    {
        var settings = new FakeSettings();
        var created = 0;
        var feed = new DesktopAppearanceFeed(() => { created++; return settings; }, NullLogger.Instance);
        using var firstTransport = new InMemoryBridgeTransport();
        using var first = new WindowContentSession(firstTransport, new WindowContentSessionOptions());
        using var secondTransport = new InMemoryBridgeTransport();
        using var second = new WindowContentSession(secondTransport, new WindowContentSessionOptions());

        var firstRegistration = feed.Add(first);
        var dark = new DesktopAppearance(DesktopColorScheme.Dark) { Contrast = DesktopContrast.More };
        await settings.EmitAsync(new PlatformResult<DesktopAppearance>.Success(dark));
        await WaitAsync(() => first.Appearance == dark, "the first window's appearance");

        // A window that opens later gets the last appearance at once.
        var secondRegistration = feed.Add(second);
        Require(second.Appearance == dark, $"The second window opened with {second.Appearance}.");
        Require(settings.Watches == 1, $"{settings.Watches} watches for two windows.");

        await settings.EmitAsync(new PlatformResult<DesktopAppearance>.Failed(PlatformFailureCode.IOError));
        await WaitAsync(() => first.Appearance is null && second.Appearance is null, "an unknown appearance after a failed read");

        // The last window that closes stops the watch; the next window starts another.
        firstRegistration.Dispose();
        firstRegistration.Dispose();
        Require(!settings.Cancelled, "The watch stopped while a window was open.");
        secondRegistration.Dispose();
        await WaitAsync(() => settings.Cancelled, "the watch to stop");
        using var thirdTransport = new InMemoryBridgeTransport();
        using var third = new WindowContentSession(thirdTransport, new WindowContentSessionOptions());
        using var thirdRegistration = feed.Add(third);
        var light = new DesktopAppearance(DesktopColorScheme.Light) { TextScale = 1.5 };
        await settings.EmitAsync(new PlatformResult<DesktopAppearance>.Success(light));
        await WaitAsync(() => third.Appearance == light, "the third window's appearance");
        Require(settings.Watches == 2 && created == 1, $"{settings.Watches} watches of {created} settings.");
        Require(first.Appearance is null, "A closed window kept following.");
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

    // Each watch reads the values emitted while it runs.
    private sealed class FakeSettings : IDesktopSettings
    {
        private Channel<PlatformResult<DesktopAppearance>> _channel = Channel.CreateUnbounded<PlatformResult<DesktopAppearance>>();

        public int Watches { get; private set; }
        public bool Cancelled { get; private set; }

        public ValueTask EmitAsync(PlatformResult<DesktopAppearance> value) => _channel.Writer.WriteAsync(value);

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
                yield return value;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
