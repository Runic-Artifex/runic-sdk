using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// One owner-bound session of the shared connection serves reads and SettingChanged signals.
// Watchers wait for a signal instead of polling; while disconnected they poll to reconnect.
internal sealed class PortalDesktopSettings(string? address = null, string destination = "org.freedesktop.portal.Desktop", PortalApplication? application = null) : DesktopSettingsSource
{
    private const string Root = "/org/freedesktop/portal/desktop";
    // The GNOME namespace carries text-scaling-factor, which GNOME's and GTK's portal backends expose; others omit it.
    private static readonly string[] Namespaces = ["org.freedesktop.appearance", "org.gnome.desktop.interface"];
    private readonly SemaphoreSlim _connect = new(1, 1);
    private readonly object _gate = new();
    private PortalConnection? _session;
    private DBusSignalSubscription[]? _subscriptions; // One per namespace.
    private CancellationTokenRegistration _ownerChanged;
    private TaskCompletionSource? _changed; // Null while no live subscription exists.
    private bool _lost;

    protected override async ValueTask<PlatformResult<DesktopAppearance>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        await _connect.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Current() ?? await ConnectAsync(cancellationToken).ConfigureAwait(false);
            var values = await ReadAllAsync(session, Namespaces, cancellationToken).ConfigureAwait(false);
            return new PlatformResult<DesktopAppearance>.Success(Read(values));
        }
        catch (Exception error) when (error is DBusException or TimeoutException or NativeBackendUnavailableException)
        {
            await ResetAsync().ConfigureAwait(false);
            return new PlatformResult<DesktopAppearance>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        finally { _connect.Release(); }
    }

    protected override Task? NextChange() { lock (_gate) return _changed?.Task; }

    protected override async ValueTask CloseCoreAsync()
    {
        await _connect.WaitAsync().ConfigureAwait(false);
        try { await ResetAsync().ConfigureAwait(false); }
        finally { _connect.Release(); }
    }

    private PortalConnection? Current()
    {
        lock (_gate) return _session is { OwnerChanged.IsCancellationRequested: false } session && !_lost ? session : null;
    }

    private async Task<PortalConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        await ResetAsync().ConfigureAwait(false);
        var session = await PortalConnection.OpenAsync(address, destination, application, cancellationToken).ConfigureAwait(false);
        // Subscribe before reading so a change between them is not missed.
        var subscriptions = new List<DBusSignalSubscription>(Namespaces.Length);
        try
        {
            foreach (string name in Namespaces)
                subscriptions.Add(await session.Connection.SubscribeAsync(new DBusSignalFilter
                {
                    Sender = session.Destination, Path = new DBusObjectPath(Root),
                    Interface = "org.freedesktop.portal.Settings", Member = "SettingChanged", Arg0 = name,
                }, cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            foreach (var added in subscriptions) await added.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        lock (_gate)
        {
            _session = session; _subscriptions = [.. subscriptions]; _lost = false;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        foreach (var subscription in subscriptions)
            _ = PortalValues.PumpAsync(subscription, _ => Changed(session, lost: false), _ => Changed(session, lost: true));
        _ownerChanged = session.OwnerChanged.Register(() => Changed(session, lost: true));
        return session;
    }

    // Values stay variants until Read checks their types; a repeated key keeps its last value, as the generated reader does.
    internal static async Task<Dictionary<string, Dictionary<string, DBusValue>>> ReadAllAsync(PortalConnection session, string[] namespaces,
        CancellationToken cancellationToken)
    {
        var values = await new Protocol.Settings(session.Connection, session.Destination, Root)
            .ReadAllAsync([.. namespaces], cancellationToken: cancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return values.ToDictionary(static entry => entry.Key, static entry => new Dictionary<string, DBusValue>(entry.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
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

    private async ValueTask ResetAsync()
    {
        DBusSignalSubscription[]? subscriptions;
        TaskCompletionSource? woken;
        lock (_gate)
        {
            (subscriptions, woken) = (_subscriptions, _changed);
            _session = null; _subscriptions = null; _changed = null;
        }
        _ownerChanged.Dispose();
        foreach (var subscription in subscriptions ?? []) await subscription.DisposeAsync().ConfigureAwait(false);
        woken?.TrySetResult();
    }
    // Values stay variants: unknown or wrongly typed optional preferences are ignored, not errors.
    internal static DesktopAppearance Read(Dictionary<string, Dictionary<string, DBusValue>> namespaces)
    {
        var result = new DesktopAppearance { TextScale = TextScale(namespaces) };
        if (!namespaces.TryGetValue("org.freedesktop.appearance", out var values)) return result;
        foreach (var (key, value) in values)
        {
            if (key == "color-scheme" && value.Signature.Value == "u")
                result = result with { ColorScheme = value.GetUInt32() switch { 1 => DesktopColorScheme.Dark, 2 => DesktopColorScheme.Light, _ => DesktopColorScheme.NoPreference } };
            if (key == "contrast" && value.Signature.Value == "u")
                result = result with { HighContrast = value.GetUInt32() switch { 0 => false, 1 => true, _ => null } };
            if (key == "reduced-motion" && value.Signature.Value == "u")
                result = result with { ReducedMotion = value.GetUInt32() switch { 0 => false, 1 => true, _ => null } };
            // An unknown or malformed optional preference is skipped.
            if (key == "accent-color" && value.Signature.Value == "(ddd)")
            {
                var fields = value.GetFields();
                var color = (fields[0].GetDouble(), fields[1].GetDouble(), fields[2].GetDouble());
                if (color.Item1 is >= 0 and <= 1 && color.Item2 is >= 0 and <= 1 && color.Item3 is >= 0 and <= 1)
                    result = result with { AccentColor = new(color.Item1, color.Item2, color.Item3) };
            }
        }
        return result;
    }

    // GNOME's text-scaling-factor (Accessibility, Large Text) is a double from 0.5 to 3. Out of that range it is ignored.
    private static double? TextScale(Dictionary<string, Dictionary<string, DBusValue>> namespaces) =>
        namespaces.TryGetValue("org.gnome.desktop.interface", out var values)
        && values.TryGetValue("text-scaling-factor", out var value) && value.Signature.Value == "d"
        && value.GetDouble() is var factor and >= 0.5 and <= 3 ? factor : null;
}
