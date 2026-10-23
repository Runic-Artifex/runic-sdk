using System.Collections.Immutable;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Linux.Portal.LauncherEntry;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// The application's Unity launcher entry on the session bus: one exported com.canonical.Unity.LauncherEntry object per
// connection and application ID, which emits Update with application://<id>.desktop and answers Query for a dock that
// starts later. The badge and the progress belong to the application, as on macOS: the most recent window's call wins,
// and a window's badge or progress goes when it releases them. KDE Plasma's task manager, Ubuntu Dock and Dash to Dock
// show them; stock GNOME Shell shows nothing.
internal sealed class PortalLauncherEntry : LauncherEntryService
{
    private static readonly SemaphoreSlim s_gate = new(1, 1);
    private static readonly Dictionary<(DBusConnection Connection, string Id), PortalLauncherEntry> s_entries = [];

    private readonly Lock _lock = new();
    private readonly string _uri;
    private object? _badgeOwner, _progressOwner;
    private DesktopBadge? _badge;
    private DesktopProgress? _progress;

    private PortalLauncherEntry(string id) => _uri = $"application://{id}.desktop";

    // The exported path: below the application's own object path (ApplicationBusObject), so it never takes another's.
    internal static string ObjectPath(string id) => ApplicationBusObject.ObjectPath(id) + "/LauncherEntry";

    internal static async Task<PortalLauncherEntry> AcquireAsync(DBusConnection connection, string id, CancellationToken cancellationToken)
    {
        await s_gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!s_entries.TryGetValue((connection, id), out var entry) || connection.Disconnected.IsCancellationRequested)
            {
                entry = new PortalLauncherEntry(id);
                // The export stays for the connection's lifetime, so a dock that starts later can still query it.
                _ = connection.Export(new DBusObjectPath(ObjectPath(id)), entry);
                s_entries[(connection, id)] = entry;
            }
            return entry;
        }
        finally { s_gate.Release(); }
    }

    // Shows the badge for owner, or removes it when null; returns whether the signal was sent.
    internal bool SetBadge(object owner, DesktopBadge? badge)
    {
        lock (_lock)
        {
            _badge = badge;
            _badgeOwner = badge is null ? null : owner;
            return Emit();
        }
    }

    internal bool SetProgress(object owner, DesktopProgress? progress)
    {
        lock (_lock)
        {
            _progress = progress;
            _progressOwner = progress is null ? null : owner;
            return Emit();
        }
    }

    // Removes what owner still shows.
    internal void Release(object owner)
    {
        lock (_lock)
        {
            bool changed = false;
            if (ReferenceEquals(_badgeOwner, owner)) { _badge = null; _badgeOwner = null; changed = true; }
            if (ReferenceEquals(_progressOwner, owner)) { _progress = null; _progressOwner = null; changed = true; }
            if (changed) Emit();
        }
    }

    private bool Emit() => EmitUpdate(_uri, Properties(_badge, _progress));

    // A count shows with count-visible; a dot, which docks have no badge for, asks for attention with urgent. Progress
    // shows only with a value: the API has no indeterminate or paused state.
    internal static ImmutableDictionary<string, DBusValue> Properties(DesktopBadge? badge, DesktopProgress? progress) =>
        ImmutableDictionary.CreateRange(StringComparer.Ordinal, new KeyValuePair<string, DBusValue>[]
        {
            new("count", DBusValue.From((long)(badge?.Count ?? 0))),
            new("count-visible", DBusValue.From(badge?.Count is not null)),
            new("urgent", DBusValue.From(badge is { Count: null })),
            new("progress", DBusValue.From(progress?.Value ?? 0d)),
            new("progress-visible", DBusValue.From(progress?.Value is not null)),
        });

    protected override ValueTask<(string AppUri, ImmutableDictionary<string, DBusValue> Properties)> QueryAsync(DBusMethodContext context)
    {
        lock (_lock) return ValueTask.FromResult((_uri, Properties(_badge, _progress)));
    }
}

// One window's view of the launcher entry. Without an application ID there is no entry to name, so it reports
// BackendUnavailable and the launcher-entry-application-id-required diagnostic.
internal sealed class PortalWindowTaskbar(PortalApplication application, string? address = null) : IWindowTaskbar
{
    private PortalLauncherEntry? _entry;
    private bool _diagnosed;

    public CapabilityStatus Status => application.ApplicationId is null
        ? new CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable) : new CapabilityStatus.Available();

    public ValueTask<PlatformResult<PlatformUnit>> SetBadgeAsync(DesktopBadge? badge, CancellationToken cancellationToken) =>
        SetAsync(entry => entry.SetBadge(this, badge), cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetProgressAsync(DesktopProgress? progress, CancellationToken cancellationToken) =>
        SetAsync(entry => entry.SetProgress(this, progress), cancellationToken);

    private async ValueTask<PlatformResult<PlatformUnit>> SetAsync(Func<PortalLauncherEntry, bool> set, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (application.ApplicationId is not { } id)
        {
            if (!_diagnosed)
            {
                _diagnosed = true;
                application.Diagnose("launcher-entry-application-id-required", "Badges and progress name the application's desktop entry, which needs its ID.",
                    "Pass the application's ID to AddRunicPlatformServices(applicationId), or run it as a Flatpak, whose ID is used.");
            }
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        try
        {
            // The shared connection, replaced after it closed, and its entry.
            var connection = (await PortalBus.Session(address, PortalConnection.RegisteredIdentity(application)).ConnectAsync(cancellationToken)
                .ConfigureAwait(false)).Connection;
            var entry = await PortalLauncherEntry.AcquireAsync(connection, id, cancellationToken).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _entry, entry) is { } previous && !ReferenceEquals(previous, entry)) previous.Release(this);
            return set(entry) ? new PlatformResult<PlatformUnit>.Success(new PlatformUnit())
                : new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        catch (Exception error) when (error is NativeBackendUnavailableException or TimeoutException or DBusException)
        {
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
    }

    public ValueTask DisposeAsync()
    {
        _entry?.Release(this);
        return ValueTask.CompletedTask;
    }
}
