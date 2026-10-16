using System.Runtime.Versioning;
using System.Threading.Channels;
using Runic.Desktop;
using Runic.Desktop.Gtk4;
using Tmds.DBus.Protocol;

// --announcement gtk3|gtk4: DesktopWindow.AnnounceAsync reaches AT-SPI as Object:Announcement with the politeness,
// as Orca receives it (#318). Run it in a session with an accessibility bus, for example
// `dbus-run-session -- xvfb-run -a`; it registers for the event as a screen reader does.
[SupportedOSPlatform("linux")]
internal static class AnnouncementSmoke
{
    private const int Polite = 1;
    private const int Assertive = 2;

    internal static int Run(string toolkit)
    {
        var options = toolkit switch
        {
            "gtk3" => new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } },
            "gtk4" => new DesktopHostOptions().WithGtk4(),
            _ => throw new ArgumentException($"Unknown toolkit '{toolkit}'; use gtk3 or gtk4.", nameof(toolkit)),
        };
        options = options with { WaitForConnection = true };
        return DesktopEventLoop.Run(options, async host =>
        {
            await using var listener = await AtSpiAnnouncements.ListenAsync();
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("<!doctype html><title>Runic announcement smoke</title><script src=\"webui.js\"></script><p>Announcements"),
            });
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
            if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.Announcements))
                throw new InvalidOperationException($"The {toolkit} window does not report announcements.");
            await ExpectAsync(window, listener, "Runic polite announcement", DesktopAnnouncementPriority.Polite, Polite);
            await ExpectAsync(window, listener, "Runic assertive announcement", DesktopAnnouncementPriority.Assertive, Assertive);
            Console.WriteLine($"PASS {toolkit} announcements reach AT-SPI as Object:Announcement with their politeness.");
            return 0;
        });
    }

    // The bridge learns of the listener's registration asynchronously, so the first announcements can go unsent.
    private static async Task ExpectAsync(DesktopWindow window, AtSpiAnnouncements listener, string message,
        DesktopAnnouncementPriority priority, int politeness)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await window.AnnounceAsync(message, priority);
            using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                while (true)
                {
                    var received = await listener.Events.ReadAsync(wait.Token);
                    if (received.Text != message) continue;
                    if (received.Politeness != politeness)
                        throw new InvalidOperationException($"'{message}' arrived with politeness {received.Politeness}, not {politeness}.");
                    return;
                }
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
        }
        throw new InvalidOperationException($"'{message}' did not reach AT-SPI as Object:Announcement.");
    }
}

// A screen reader's view of the accessibility bus: registers for object:announcement and reads the events.
internal sealed class AtSpiAnnouncements : IAsyncDisposable
{
    private readonly DBusConnection _session;
    private readonly DBusConnection _accessibility;
    private readonly IDisposable _subscription;

    private AtSpiAnnouncements(DBusConnection session, DBusConnection accessibility, IDisposable subscription,
        Channel<(string Text, int Politeness)> events)
    {
        _session = session;
        _accessibility = accessibility;
        _subscription = subscription;
        Events = events.Reader;
    }

    internal ChannelReader<(string Text, int Politeness)> Events { get; }

    internal static async Task<AtSpiAnnouncements> ListenAsync()
    {
        var session = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No session bus."));
        await session.ConnectAsync();
        var address = await session.CallMethodAsync(GetAddress(session), static (message, _) => message.GetBodyReader().ReadString());
        var accessibility = new DBusConnection(address);
        await accessibility.ConnectAsync();
        var events = Channel.CreateUnbounded<(string Text, int Politeness)>();
        var subscription = await accessibility.AddMatchAsync(new MatchRule
        {
            Type = MessageType.Signal, Interface = "org.a11y.atspi.Event.Object", Member = "Announcement",
        }, static (message, _) =>
        {
            var reader = message.GetBodyReader();
            _ = reader.ReadString();
            var politeness = reader.ReadInt32();
            _ = reader.ReadInt32();
            return (reader.ReadVariantValue().GetString(), politeness);
        }, value =>
        {
            if (value.HasValue) events.Writer.TryWrite(value.Value);
        }, emitOnCapturedContext: false);
        await accessibility.CallMethodAsync(RegisterEvent(accessibility));
        return new AtSpiAnnouncements(session, accessibility, subscription, events);
    }

    private static MessageBuffer GetAddress(DBusConnection session)
    {
        using var writer = session.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.a11y.Bus", path: "/org/a11y/bus", @interface: "org.a11y.Bus", member: "GetAddress");
        return writer.CreateMessage();
    }

    private static MessageBuffer RegisterEvent(DBusConnection accessibility)
    {
        using var writer = accessibility.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.a11y.atspi.Registry", path: "/org/a11y/atspi/registry",
            @interface: "org.a11y.atspi.Registry", member: "RegisterEvent", signature: "s");
        writer.WriteString("object:announcement");
        return writer.CreateMessage();
    }

    public ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        _accessibility.Dispose();
        _session.Dispose();
        return ValueTask.CompletedTask;
    }
}
