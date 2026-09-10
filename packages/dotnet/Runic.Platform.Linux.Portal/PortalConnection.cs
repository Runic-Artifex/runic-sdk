using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;

namespace Runic.Platform.Linux.Portal;

// A connection's identity belongs to one portal process. Binding every call to
// that owner prevents a restart between registration and submission from sending
// an anonymous request to the replacement process.
internal sealed class PortalConnection : IDisposable
{
    internal DBusConnection Connection { get; }
    internal string Destination { get; private set; }
    internal CancellationToken OwnerChanged { get; private set; }
    private NameOwnerWatcher? _watcher;

    private PortalConnection(DBusConnection connection, string destination)
    { Connection = connection; Destination = destination; }

    internal static async Task<PortalConnection> OpenAsync(string? address, string destination,
        PortalApplication? application, CancellationToken cancellationToken)
    {
        var session = new PortalConnection(new DBusConnection(address ?? DBusAddress.Session ?? throw new NativeBackendUnavailableException()), destination);
        try
        {
            await session.Connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!destination.StartsWith(':'))
            {
                session._watcher = await session.Connection.WatchNameOwnerAsync(destination).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                if (session._watcher.GetCurrentOwner() is null)
                    await session.Connection.CallMethodAsync(StartService(session.Connection, destination), static (message, _) => message.GetBodyReader().ReadUInt32())
                        .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                session.Destination = await session._watcher.WaitForOwnerAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                session.OwnerChanged = session._watcher.GetOwnerChangedCancellationToken(session.Destination);
            }
            if (application?.ApplicationId is { } id && !File.Exists("/.flatpak-info") && Environment.GetEnvironmentVariable("SNAP") is null)
            {
                try
                {
                    await new Protocol.Registry(session.Connection, session.Destination, "/org/freedesktop/portal/desktop")
                        .RegisterAsync(id, new()).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (DBusErrorReplyException)
                {
                    application.Diagnose("portal-identity-registration-failed", "The portal rejected the application's desktop identity.",
                        $"Install a matching {id}.desktop entry visible to the desktop session. For development, omit the explicit application ID to use desktop-inferred identity.");
                    throw;
                }
                application.Diagnose("portal-identity-registered", "The portal connection registered its desktop identity.", "All services created by this application use the same desktop ID.");
            }
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    private static MessageBuffer StartService(DBusConnection connection, string name)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus", @interface: "org.freedesktop.DBus",
            member: "StartServiceByName", signature: "su");
        writer.WriteString(name); writer.WriteUInt32(0);
        return writer.CreateMessage();
    }

    public void Dispose() { _watcher?.Dispose(); Connection.Dispose(); }
}
