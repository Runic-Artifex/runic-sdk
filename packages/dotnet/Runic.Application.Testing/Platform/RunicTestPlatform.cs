using System.Collections.Immutable;
using Runic.Platform;

namespace Runic.Application.Testing;

/// <summary>One file or URI the application handed to a native application.</summary>
/// <param name="Path">The local path, or <see langword="null"/> for a URI.</param>
/// <param name="Uri">The URI, or <see langword="null"/> for a path.</param>
/// <param name="Operation">The file operation; <see cref="DesktopFileOperation.Open"/> for a URI.</param>
public sealed record RunicLaunchCall(string? Path, Uri? Uri, DesktopFileOperation Operation);

/// <summary>One screen-reader announcement the application made.</summary>
/// <param name="Message">The text.</param>
/// <param name="Priority">Its priority.</param>
public sealed record RunicAnnouncementCall(string Message, PlatformAnnouncementPriority Priority);

/// <summary>
/// The platform services of one test window: scripted, recording fakes of <see cref="IWindowPlatformServices"/> for
/// <see cref="RunicWindowTestHost{TViewModel}"/>, a serve-only browser test, or a ViewModel a test builds itself.
/// </summary>
/// <remarks>
/// <para>
/// A ViewModel can take this as <see cref="IWindowPlatformServices"/> or take its parts directly:
/// <see cref="FileDialogs"/> as <see cref="IFileDialogs"/>, <see cref="Clipboard"/> as <see cref="ITextClipboard"/> or
/// <see cref="IDesktopClipboard"/>, <see cref="MessageBoxes"/> as <see cref="IDesktopMessageBoxes"/>, the launcher as
/// <see cref="IDesktopFileLauncher"/>, and <see cref="Notifications"/> as <see cref="IDesktopNotifications"/>.
/// <c>services.AddRunicTestPlatform()</c> registers them for each window scope.
/// </para>
/// <para>
/// The services are open from the start, as a window's are once it opened. <see cref="Close"/>, which disposing calls,
/// acts as the window closing: every service then reports <see cref="PlatformUnavailableReason.OwnerClosed"/>, file
/// leases are released and clipboard watches end. Monitors, the menu bar, taskbar badges and progress, and drags report
/// <see cref="PlatformUnavailableReason.BackendUnavailable"/>, as on a window without them.
/// </para>
/// </remarks>
public sealed class RunicTestPlatform : IWindowPlatformServices, IDesktopFileLauncher, IDisposable, IAsyncDisposable
{
    private static readonly IWindowPlatformServices s_checks = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
    private static readonly ImmutableHashSet<string> s_unsupported =
    [
        PlatformCapabilityIds.MenuBar, PlatformCapabilityIds.Monitors, PlatformCapabilityIds.TaskbarBadge,
        PlatformCapabilityIds.TaskbarProgress, PlatformCapabilityIds.DragOut,
    ];

    private readonly object _gate = new();
    private readonly List<RunicLaunchCall> _launches = [];
    private readonly List<RunicAnnouncementCall> _announcements = [];
    private readonly Guid _generation = Guid.NewGuid();
    private volatile bool _closed;

    /// <summary>Creates the services of one open test window, with notifications of its own.</summary>
    public RunicTestPlatform() : this(new RunicTestNotifications(), null)
    {
    }

    /// <summary>Creates the services of one open test window that share the application's notifications.</summary>
    /// <param name="notifications">The application's notifications, shared by its windows.</param>
    public RunicTestPlatform(RunicTestNotifications notifications) : this(notifications, null)
    {
    }

    internal RunicTestPlatform(RunicTestNotifications notifications, ServeOnlyPlatformChannel? channel)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        Channel = channel;
        Notifications = notifications;
        FileDialogs = new RunicTestFileDialogs(this);
        Clipboard = new RunicTestClipboard(this);
        MessageBoxes = new RunicTestMessageBoxes(this);
        ContextMenus = new RunicTestContextMenus(this);
        if (channel is not null) notifications.Recorder = (operation, values) => channel.Record("notifications", operation, values);
    }

    /// <summary>The window's scripted file dialogs.</summary>
    public RunicTestFileDialogs FileDialogs { get; }

    /// <summary>The window's in-memory clipboard.</summary>
    public RunicTestClipboard Clipboard { get; }

    /// <summary>The window's scripted message boxes.</summary>
    public RunicTestMessageBoxes MessageBoxes { get; }

    /// <summary>The window's scripted context menus, for <see cref="ShowContextMenuAsync"/>.</summary>
    public RunicTestContextMenus ContextMenus { get; }

    /// <summary>The application's recording notifications.</summary>
    public RunicTestNotifications Notifications { get; }

    /// <summary>The files and URIs handed to native applications, oldest first.</summary>
    public IReadOnlyList<RunicLaunchCall> Launches
    {
        get { lock (_gate) return [.. _launches]; }
    }

    /// <summary>What <see cref="IDesktopFileLauncher"/> calls report while the window is open; success by default.</summary>
    public PlatformResult<PlatformUnit> LaunchResult { get; set; } = new PlatformResult<PlatformUnit>.Success(default);

    /// <summary>The screen-reader announcements made, oldest first.</summary>
    public IReadOnlyList<RunicAnnouncementCall> Announcements
    {
        get { lock (_gate) return [.. _announcements]; }
    }

    /// <summary>Whether <see cref="Close"/> ran.</summary>
    public bool IsClosed => _closed;

    internal ServeOnlyPlatformChannel? Channel { get; }

    IFileDialogs IWindowPlatformServices.FileDialogs => FileDialogs;
    ITextClipboard IWindowPlatformServices.Clipboard => Clipboard;
    IDesktopFileLauncher IWindowPlatformServices.FileLauncher => this;
    IDesktopMessageBoxes IWindowPlatformServices.MessageBoxes => MessageBoxes;
    IDesktopMonitors IWindowPlatformServices.Monitors => DesktopMonitors.Unavailable(Reason(PlatformUnavailableReason.BackendUnavailable));

    /// <inheritdoc />
    public CapabilitySnapshot GetSnapshot() => new(_generation, WindowPlatformServices.CapabilityIds.ToImmutableDictionary(
        static id => id,
        id => _closed ? new CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerClosed)
            : s_unsupported.Contains(id) ? new CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable)
            : id == PlatformCapabilityIds.ContextMenu && ContextMenus.UnavailableReason is { } menus ? new CapabilityStatus.Unavailable(menus)
            : id.StartsWith("platform.clipboard.", StringComparison.Ordinal) && Clipboard.UnavailableReason is { } clipboard
                ? new CapabilityStatus.Unavailable(clipboard)
            : (CapabilityStatus)new CapabilityStatus.Available()));

    /// <inheritdoc />
    public ValueTask<PlatformResult<DesktopMenuItem?>> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items,
        DesktopContextMenuOptions? options = null, CancellationToken cancellationToken = default) =>
        ContextMenus.ShowAsync(items, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<PlatformResult<PlatformUnit>> AnnounceAsync(string message,
        PlatformAnnouncementPriority priority = PlatformAnnouncementPriority.Polite, CancellationToken cancellationToken = default)
    {
        await s_checks.AnnounceAsync(message, priority, cancellationToken).ConfigureAwait(false);
        if (_closed) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        lock (_gate) _announcements.Add(new RunicAnnouncementCall(message, priority));
        Record("announcements", "announce", [("message", message), ("priority", priority.ToString())]);
        return new PlatformResult<PlatformUnit>.Success(default);
    }

    /// <inheritdoc />
    public async ValueTask<PlatformResult<DesktopDragEffects>> StartDragAsync(DesktopDragData data, CancellationToken cancellationToken = default)
    {
        await s_checks.StartDragAsync(data, cancellationToken).ConfigureAwait(false);
        return new PlatformResult<DesktopDragEffects>.Unavailable(Reason(PlatformUnavailableReason.BackendUnavailable));
    }

    /// <inheritdoc />
    public async ValueTask<PlatformResult<PlatformUnit>> SetBadgeAsync(DesktopBadge? badge, CancellationToken cancellationToken = default)
    {
        await s_checks.SetBadgeAsync(badge, cancellationToken).ConfigureAwait(false);
        return new PlatformResult<PlatformUnit>.Unavailable(Reason(PlatformUnavailableReason.BackendUnavailable));
    }

    /// <inheritdoc />
    public async ValueTask<PlatformResult<PlatformUnit>> SetProgressAsync(DesktopProgress? progress, CancellationToken cancellationToken = default)
    {
        await s_checks.SetProgressAsync(progress, cancellationToken).ConfigureAwait(false);
        return new PlatformResult<PlatformUnit>.Unavailable(Reason(PlatformUnavailableReason.BackendUnavailable));
    }

    /// <inheritdoc />
    public async ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open,
        CancellationToken cancellationToken = default)
    {
        await s_checks.FileLauncher.LaunchAsync(path, operation, cancellationToken).ConfigureAwait(false);
        return Launched(new RunicLaunchCall(path, null, operation));
    }

    /// <inheritdoc />
    public async ValueTask<PlatformResult<PlatformUnit>> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        await s_checks.FileLauncher.LaunchUriAsync(uri, cancellationToken).ConfigureAwait(false);
        return Launched(new RunicLaunchCall(null, uri, DesktopFileOperation.Open));
    }

    private async ValueTask CloseCoreAsync()
    {
        if (_closed) return;
        _closed = true;
        Clipboard.Close();
        await FileDialogs.ReleaseAllAsync().ConfigureAwait(false);
    }

    /// <summary>Closes the window's services: they report <see cref="PlatformUnavailableReason.OwnerClosed"/> from now on.</summary>
    /// <remarks>Releases every file lease and ends every clipboard watch. Closing again does nothing.</remarks>
    public void Close() => CloseCoreAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Closes the window's services; see <see cref="Close"/>.</summary>
    public void Dispose() => Close();

    /// <summary>Closes the window's services; see <see cref="Close"/>.</summary>
    /// <returns>A task that completes once every lease is released.</returns>
    public ValueTask DisposeAsync() => CloseCoreAsync();

    private PlatformResult<PlatformUnit> Launched(RunicLaunchCall call)
    {
        if (_closed) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        lock (_gate) _launches.Add(call);
        Record("launcher", call.Uri is null ? "launch" : "launchUri",
            [("path", call.Path), ("uri", call.Uri?.ToString()), ("operation", call.Operation.ToString())]);
        return LaunchResult;
    }

    private PlatformUnavailableReason Reason(PlatformUnavailableReason open) => _closed ? PlatformUnavailableReason.OwnerClosed : open;

    // Applies the scripted responses a serve-only test appended since the last call.
    internal void Pump() => Channel?.Pump(this);

    // Reports a call to a serve-only test.
    internal void Record(string service, string operation, (string Name, string? Value)[] values) =>
        Channel?.Record(service, operation, values);
}
