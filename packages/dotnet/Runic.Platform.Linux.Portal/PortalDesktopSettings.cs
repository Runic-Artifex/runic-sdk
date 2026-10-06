using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;

namespace Runic.Platform.Linux.Portal;

// One owner-bound connection serves reads and SettingChanged signals. Watchers wait
// for a signal instead of polling; while disconnected they poll to reconnect.
internal sealed class PortalDesktopSettings(string? address = null, string destination = "org.freedesktop.portal.Desktop", PortalApplication? application = null) : DesktopSettingsSource
{
    private const string Root = "/org/freedesktop/portal/desktop";
    private static readonly string[] Namespaces = ["org.freedesktop.appearance"];
    private readonly SemaphoreSlim _connect = new(1, 1);
    private readonly object _gate = new();
    private PortalConnection? _session;
    private IDisposable? _subscription;
    private CancellationTokenRegistration _ownerChanged;
    private TaskCompletionSource? _changed; // Null while no live subscription exists.
    private bool _lost;

    protected override async ValueTask<PlatformResult<DesktopAppearance>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        await _connect.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Current() ?? await ConnectAsync(cancellationToken).ConfigureAwait(false);
            var connection = session.Connection;
            return new PlatformResult<DesktopAppearance>.Success(await connection.CallMethodAsync(Request(connection, session.Destination),
                static (message, _) => Read(message)).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (error is DBusExceptionBase or TimeoutException or NativeBackendUnavailableException)
        {
            Reset();
            return new PlatformResult<DesktopAppearance>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        finally { _connect.Release(); }
    }

    protected override Task? NextChange() { lock (_gate) return _changed?.Task; }

    protected override async ValueTask CloseCoreAsync()
    {
        await _connect.WaitAsync().ConfigureAwait(false);
        try { Reset(); }
        finally { _connect.Release(); }
    }

    private PortalConnection? Current()
    {
        lock (_gate) return _session is { OwnerChanged.IsCancellationRequested: false } session && !_lost ? session : null;
    }

    private async Task<PortalConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        Reset();
        var session = await PortalConnection.OpenAsync(address, destination, application, cancellationToken).ConfigureAwait(false);
        try
        {
            // Subscribe before reading so a change between them is not missed.
            var subscription = await session.Connection.AddMatchAsync(new MatchRule
            {
                Type = MessageType.Signal, Sender = session.Destination, Path = Root,
                Interface = "org.freedesktop.portal.Settings", Member = "SettingChanged", Arg0 = Namespaces[0],
            }, static (_, _) => true, notification => Changed(session, lost: notification.IsCompletion),
                emitOnCapturedContext: false, flags: ObserverFlags.EmitAll).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _session = session; _subscription = subscription; _lost = false;
                _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _ownerChanged = session.OwnerChanged.Register(() => Changed(session, lost: true));
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    // Wakes watchers of the current connection; a lost connection is replaced by the next read.
    private void Changed(PortalConnection source, bool lost)
    {
        TaskCompletionSource? woken;
        lock (_gate)
        {
            if (!ReferenceEquals(source, _session)) return;
            woken = _changed;
            if (lost) _lost = true;
            _changed = lost ? null : new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        woken?.TrySetResult();
    }

    private void Reset()
    {
        PortalConnection? session;
        IDisposable? subscription;
        TaskCompletionSource? woken;
        lock (_gate)
        {
            (session, subscription, woken) = (_session, _subscription, _changed);
            _session = null; _subscription = null; _changed = null;
        }
        _ownerChanged.Dispose();
        subscription?.Dispose();
        session?.Dispose();
        woken?.TrySetResult();
    }
    private static MessageBuffer Request(DBusConnection connection, string peer)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: peer, path: "/org/freedesktop/portal/desktop",
            @interface: "org.freedesktop.portal.Settings", member: "ReadAll", signature: "as");
        writer.WriteArray(Namespaces);
        return writer.CreateMessage();
    }
    internal static DesktopAppearance Read(Message message)
    {
        var result = new DesktopAppearance();
        var reader = message.GetBodyReader();
        var namespaces = reader.ReadDictionaryStart();
        while (reader.HasNext(namespaces))
        {
            var ns = reader.ReadString();
            var values = reader.ReadDictionaryStart();
            while (reader.HasNext(values))
            {
                var key = reader.ReadString();
                var value = reader.ReadVariantValue();
                if (ns != "org.freedesktop.appearance") continue;
                if (key == "color-scheme" && value.Type == VariantValueType.UInt32)
                    result = result with { ColorScheme = value.GetUInt32() switch { 1 => DesktopColorScheme.Dark, 2 => DesktopColorScheme.Light, _ => DesktopColorScheme.NoPreference } };
                if (key == "contrast" && value.Type == VariantValueType.UInt32)
                    result = result with { HighContrast = value.GetUInt32() switch { 0 => false, 1 => true, _ => null } };
                if (key == "reduced-motion" && value.Type == VariantValueType.UInt32)
                    result = result with { ReducedMotion = value.GetUInt32() switch { 0 => false, 1 => true, _ => null } };
                if (key == "accent-color" && value.Type == VariantValueType.Struct)
                {
                    try
                    {
                        if (value.Count != 3) continue;
                        var color = (value.GetItem(0).GetDouble(), value.GetItem(1).GetDouble(), value.GetItem(2).GetDouble());
                        if (color.Item1 is >= 0 and <= 1 && color.Item2 is >= 0 and <= 1 && color.Item3 is >= 0 and <= 1)
                            result = result with { AccentColor = new(color.Item1, color.Item2, color.Item3) };
                    }
                    catch (InvalidOperationException) { /* Unknown/malformed optional preference. */ }
                }
            }
        }
        return result;
    }
}
