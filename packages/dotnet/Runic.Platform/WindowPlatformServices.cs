using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>Native services owned by one application window.</summary>
/// <remarks>
/// A host registers one instance per window scope, so a ViewModel can take this interface, or
/// <see cref="IFileDialogs"/>, <see cref="ITextClipboard"/>, <see cref="IDesktopClipboard"/> or <see cref="IDesktopFileLauncher"/> directly, in its
/// constructor. The services follow the window: before it opens they report
/// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>, and once it closes
/// <see cref="PlatformUnavailableReason.OwnerClosed"/>. A host without a native owner reports every service
/// unavailable instead of failing to resolve it.
/// </remarks>
public interface IWindowPlatformServices : IPlatformCapabilities
{
    /// <summary>File and directory dialogs owned by the window.</summary>
    IFileDialogs FileDialogs { get; }
    /// <summary>The window's clipboard, used through the window's native thread.</summary>
    /// <remarks>
    /// The clipboard of Runic's hosts is also an <see cref="IDesktopClipboard"/>, with HTML, RTF, images, files and a
    /// watch: <c>services.Clipboard as IDesktopClipboard</c>.
    /// </remarks>
    ITextClipboard Clipboard { get; }
    /// <summary>Hands local files and web, mail or Windows Settings URIs to native applications on behalf of the window.</summary>
    IDesktopFileLauncher FileLauncher { get; }

    /// <summary>Asks the screen reader to announce a message for the window, such as the result of an action.</summary>
    /// <param name="message">The text to speak, in the window's language.</param>
    /// <param name="priority">
    /// <see cref="PlatformAnnouncementPriority.Polite"/>, the default, waits for what the screen reader is saying;
    /// <see cref="PlatformAnnouncementPriority.Assertive"/> interrupts it.
    /// </param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> once the window's accessibility API accepted the announcement, or
    /// <see cref="PlatformResult{T}.Unavailable"/>: <see cref="PlatformUnavailableReason.OwnerUnavailable"/> before the
    /// window opens or on a host without a native owner, <see cref="PlatformUnavailableReason.OwnerClosed"/> once it
    /// closed, and <see cref="PlatformUnavailableReason.BackendUnavailable"/> when the window cannot announce.
    /// </returns>
    /// <remarks>
    /// The host-neutral counterpart of an ARIA live region: the host raises the announcement through the platform's
    /// accessibility API for the window, so it reaches the screen reader where a live region in the page does not.
    /// An implementation that does not override this member reports
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="message"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="priority"/> is not a defined value.</exception>
    ValueTask<PlatformResult<PlatformUnit>> AnnounceAsync(string message,
        PlatformAnnouncementPriority priority = PlatformAnnouncementPriority.Polite, CancellationToken cancellationToken = default)
    {
        WindowPlatformServices.CheckAnnouncement(message, priority, cancellationToken);
        return ValueTask.FromResult<PlatformResult<PlatformUnit>>(
            new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable));
    }

    /// <summary>Shows a native context menu for the window and returns the item the user chose.</summary>
    /// <param name="items">
    /// The menu's entries: the rules of <see cref="DesktopMenuItem"/> and <see cref="DesktopMenu"/>, at most four nested
    /// submenus and 256 entries, and at least one entry. Command ids may repeat; <see cref="DesktopMenu.Role"/> is ignored.
    /// </param>
    /// <param name="options">Where the menu opens; <see langword="null"/> opens it at the pointer.</param>
    /// <param name="cancellationToken">Closes the menu, which then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> with the chosen item, one of <paramref name="items"/>, or with
    /// <see langword="null"/> when the user dismissed the menu, a later context menu of the window replaced it, or it
    /// closed with the window. <see cref="PlatformResult{T}.Unavailable"/>:
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/> before the window opens or on a host without a native
    /// owner, <see cref="PlatformUnavailableReason.OwnerClosed"/> once it closed, and
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/> when the window has no native context menus
    /// (<see cref="PlatformCapabilityIds.ContextMenu"/>).
    /// </returns>
    /// <remarks>
    /// <para>
    /// The menu is modal for the window. Disabled items and items of disabled submenus cannot be chosen; a shortcut is
    /// shown as a hint and not registered. The result is the item record, so the caller runs its command, for example
    /// with <see cref="DesktopMenuItem.CommandParameter"/> as the row the menu is for.
    /// </para>
    /// <para>
    /// A page asks for the menu through an ordinary command, for example <c>showRowMenu(key, x, y)</c> from its
    /// <c>contextmenu</c> event, after calling <c>preventDefault()</c> so the web view's own menu stays closed.
    /// An implementation that does not override this member checks the arguments and reports
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="items"/> is empty or breaks a menu rule, or <paramref name="options"/> sets only one coordinate or one
    /// that is not a finite number.
    /// </exception>
    ValueTask<PlatformResult<DesktopMenuItem?>> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items,
        DesktopContextMenuOptions? options = null, CancellationToken cancellationToken = default)
    {
        WindowPlatformServices.CheckContextMenu(items, options, cancellationToken);
        return ValueTask.FromResult<PlatformResult<DesktopMenuItem?>>(
            new PlatformResult<DesktopMenuItem?>.Unavailable(PlatformUnavailableReason.OwnerUnavailable));
    }

    /// <summary>Starts a native drag of files, text or URIs out of the window, and returns the effect of the drop.</summary>
    /// <param name="data">What to drag; the rules of <see cref="DesktopDragData"/>.</param>
    /// <param name="cancellationToken">Cancels before the drag starts; a running drag ends with the pointer.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> with the effect the drop target chose, one of
    /// <see cref="DesktopDragData.AllowedEffects"/>, or <see cref="DesktopDragEffects.None"/> when the user cancelled the
    /// drag, dropped it where nothing accepted it, or no mouse button was held when the call arrived.
    /// <see cref="PlatformResult{T}.Unavailable"/>: <see cref="PlatformUnavailableReason.OwnerUnavailable"/> before the
    /// window opens or on a host without a native owner, <see cref="PlatformUnavailableReason.OwnerClosed"/> once it
    /// closed, and <see cref="PlatformUnavailableReason.BackendUnavailable"/> when the window cannot start drags
    /// (<see cref="PlatformCapabilityIds.DragOut"/>).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Call it while the user holds the primary mouse button on the window: a page asks for the drag through an
    /// ordinary command from its <c>dragstart</c> event, after calling <c>preventDefault()</c> so the web view starts no
    /// drag of its own, and the ViewModel builds <paramref name="data"/> from its own state, for example the files of the
    /// row the command names. The paths never come from the page.
    /// </para>
    /// <para>
    /// <see cref="DesktopDragEffects.Move"/> means the target asked for a move: the files may already be gone, for
    /// example after an optimized move on Windows, so delete what remains. An implementation that does not override this
    /// member checks the arguments and reports <see cref="PlatformUnavailableReason.OwnerUnavailable"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="data"/> breaks a rule of <see cref="DesktopDragData"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    ValueTask<PlatformResult<DesktopDragEffects>> StartDragAsync(DesktopDragData data, CancellationToken cancellationToken = default)
    {
        DesktopDragRules.Check(data);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PlatformResult<DesktopDragEffects>>(
            new PlatformResult<DesktopDragEffects>.Unavailable(PlatformUnavailableReason.OwnerUnavailable));
    }

    /// <summary>The desktop's monitors, in the units of the window's position and size (<see cref="PlatformCapabilityIds.Monitors"/>).</summary>
    /// <remarks>
    /// The monitors of the window's host; the list does not depend on the window. An implementation that does not
    /// override this member reports <see cref="PlatformUnavailableReason.OwnerUnavailable"/>.
    /// </remarks>
    IDesktopMonitors Monitors => DesktopMonitors.Unavailable(PlatformUnavailableReason.OwnerUnavailable);

    /// <summary>Native message boxes owned by the window (<see cref="PlatformCapabilityIds.MessageBoxes"/>).</summary>
    /// <remarks>
    /// A box is modal to the window and closes with it. An implementation that does not override this member reports
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>, as hosts without a native owner, such as CS-WebUI, do.
    /// </remarks>
    IDesktopMessageBoxes MessageBoxes => DesktopMessageBoxes.Unavailable(PlatformUnavailableReason.OwnerUnavailable);

    /// <summary>Shows a badge on the window's taskbar button or the application's Dock icon (<see cref="PlatformCapabilityIds.TaskbarBadge"/>).</summary>
    /// <param name="badge">The count or dot to show; <see langword="null"/> removes the badge.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> once the desktop took the badge, also on a desktop that does not show one,
    /// such as GNOME without a dock extension, or <see cref="PlatformResult{T}.Unavailable"/>:
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/> before the window opens or on a host without a native
    /// owner, <see cref="PlatformUnavailableReason.OwnerClosed"/> once it closed, and
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/> when the desktop has no badges.
    /// </returns>
    /// <remarks>
    /// Windows shows the badge on this window's taskbar button, drawn by Runic as an overlay icon with
    /// <see cref="DesktopBadge.Description"/> as its accessible text. macOS and Linux have one badge for the application:
    /// the most recent call of any window wins, and a window's badge goes when the window closes. On Linux the badge
    /// needs the application's ID, as for notifications. An implementation that does not override this member checks
    /// the argument and reports <see cref="PlatformUnavailableReason.OwnerUnavailable"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="badge"/> has a count below 1 or a description with a NUL character.</exception>
    ValueTask<PlatformResult<PlatformUnit>> SetBadgeAsync(DesktopBadge? badge, CancellationToken cancellationToken = default)
    {
        DesktopTaskbarRules.Check(badge, cancellationToken);
        return ValueTask.FromResult<PlatformResult<PlatformUnit>>(
            new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable));
    }

    /// <summary>Shows progress on the window's taskbar button or the application's Dock icon (<see cref="PlatformCapabilityIds.TaskbarProgress"/>).</summary>
    /// <param name="progress">The progress to show; <see langword="null"/> removes it.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns>The results of <see cref="SetBadgeAsync"/>.</returns>
    /// <remarks>
    /// Windows fills this window's taskbar button, yellow for <see cref="DesktopProgressState.Paused"/> and red for
    /// <see cref="DesktopProgressState.Error"/>. macOS draws a bar on the Dock icon and Linux docks on their launcher, for
    /// the application as with badges. An implementation that does not override this member checks the argument and
    /// reports <see cref="PlatformUnavailableReason.OwnerUnavailable"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="progress"/> has a value outside 0 to 1 or an undefined state.</exception>
    ValueTask<PlatformResult<PlatformUnit>> SetProgressAsync(DesktopProgress? progress, CancellationToken cancellationToken = default)
    {
        DesktopTaskbarRules.Check(progress, cancellationToken);
        return ValueTask.FromResult<PlatformResult<PlatformUnit>>(
            new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable));
    }
}

/// <summary>How a screen-reader announcement relates to what the screen reader is saying, as ARIA live regions do.</summary>
/// <remarks>A closed set: <c>aria-live</c> defines these two announcing politeness levels.</remarks>
public enum PlatformAnnouncementPriority
{
    /// <summary>After what the screen reader is saying, as <c>aria-live="polite"</c>.</summary>
    Polite = 0,

    /// <summary>Interrupting what the screen reader is saying, as <c>aria-live="assertive"</c>; for errors and other messages the user must hear at once.</summary>
    Assertive = 1,
}

/// <summary>The capability identifiers of a window's platform services, the keys of
/// <see cref="CapabilitySnapshot.Statuses"/>.</summary>
/// <example><code>
/// if (services.GetSnapshot().Statuses[PlatformCapabilityIds.UrisLaunch] is CapabilityStatus.Available) { … }
/// </code></example>
public static class PlatformCapabilityIds
{
    /// <summary>Opening a file through <see cref="IFileDialogs.OpenFileAsync"/>.</summary>
    public const string FilesOpen = "platform.files.open";
    /// <summary>Saving a file through <see cref="IFileDialogs.SaveFileAsync"/>. Unavailable with
    /// <see cref="PlatformUnavailableReason.AtomicReplaceUnavailable"/> when saves cannot replace atomically.</summary>
    public const string FilesSave = "platform.files.save";
    /// <summary>Selecting a directory through <see cref="IFileDialogs.OpenDirectoryAsync"/>.</summary>
    public const string DirectoriesOpen = "platform.directories.open";
    /// <summary>File dialogs owned by the window.</summary>
    public const string DialogsOwned = "platform.dialogs.owned";
    /// <summary>Reading text through <see cref="ITextClipboard.ReadTextAsync"/>.</summary>
    public const string ClipboardReadText = "platform.clipboard.readText";
    /// <summary>Writing text through <see cref="ITextClipboard.WriteTextAsync"/>.</summary>
    public const string ClipboardWriteText = "platform.clipboard.writeText";
    /// <summary>Handing local files to applications through <see cref="IDesktopFileLauncher.LaunchAsync"/>.</summary>
    public const string FilesLaunch = "platform.files.launch";
    /// <summary>Selecting several files through <see cref="IFileDialogs.OpenFilesAsync"/>.</summary>
    public const string FilesOpenMultiple = "platform.files.openMultiple";
    /// <summary>Opening URIs through <see cref="IDesktopFileLauncher.LaunchUriAsync"/>.</summary>
    public const string UrisLaunch = "platform.uris.launch";
    /// <summary>The window shows its menu bar natively, in the window or in the desktop's global menu bar.</summary>
    /// <remarks>
    /// Unavailable for a window without a native bar, including one that renders the bar in its page; the in-page menu
    /// bar reports its own placement.
    /// </remarks>
    public const string MenuBar = "platform.menus.bar";
    /// <summary>Showing native context menus through <see cref="IWindowPlatformServices.ShowContextMenuAsync"/>.</summary>
    public const string ContextMenu = "platform.menus.context";
    /// <summary>Selecting several directories through <see cref="IFileDialogs.OpenDirectoriesAsync"/>.</summary>
    public const string DirectoriesOpenMultiple = "platform.directories.openMultiple";
    /// <summary>Listing and watching the desktop's monitors through <see cref="IWindowPlatformServices.Monitors"/>.</summary>
    public const string Monitors = "platform.monitors";
    /// <summary>Showing native message boxes through <see cref="IWindowPlatformServices.MessageBoxes"/>.</summary>
    public const string MessageBoxes = "platform.messageBoxes";
    /// <summary>Reading HTML, RTF, images and files through <see cref="IDesktopClipboard"/>.</summary>
    public const string ClipboardReadData = "platform.clipboard.readData";
    /// <summary>Writing through <see cref="IDesktopClipboard.WriteAsync"/>.</summary>
    public const string ClipboardWriteData = "platform.clipboard.writeData";
    /// <summary>Watching the clipboard's formats through <see cref="IDesktopClipboard.WatchAsync"/>.</summary>
    public const string ClipboardWatch = "platform.clipboard.watch";
    /// <summary>Showing a badge through <see cref="IWindowPlatformServices.SetBadgeAsync"/>.</summary>
    public const string TaskbarBadge = "platform.taskbar.badge";
    /// <summary>Showing progress through <see cref="IWindowPlatformServices.SetProgressAsync"/>.</summary>
    public const string TaskbarProgress = "platform.taskbar.progress";
    /// <summary>Starting drags out of the window through <see cref="IWindowPlatformServices.StartDragAsync"/>.</summary>
    public const string DragOut = "platform.drag.out";
}

/// <summary>Window platform services for hosts that cannot provide them.</summary>
public static class WindowPlatformServices
{
    /// <summary>The capability identifiers a window's platform services report: every <see cref="PlatformCapabilityIds"/>
    /// constant.</summary>
    public static ImmutableArray<string> CapabilityIds { get; } =
    [
        PlatformCapabilityIds.FilesOpen, PlatformCapabilityIds.FilesSave, PlatformCapabilityIds.DirectoriesOpen,
        PlatformCapabilityIds.DialogsOwned, PlatformCapabilityIds.ClipboardReadText, PlatformCapabilityIds.ClipboardWriteText,
        PlatformCapabilityIds.FilesLaunch, PlatformCapabilityIds.FilesOpenMultiple, PlatformCapabilityIds.UrisLaunch,
        PlatformCapabilityIds.MenuBar, PlatformCapabilityIds.ContextMenu,
        PlatformCapabilityIds.DirectoriesOpenMultiple, PlatformCapabilityIds.Monitors, PlatformCapabilityIds.MessageBoxes,
        PlatformCapabilityIds.ClipboardReadData, PlatformCapabilityIds.ClipboardWriteData, PlatformCapabilityIds.ClipboardWatch,
        PlatformCapabilityIds.TaskbarBadge, PlatformCapabilityIds.TaskbarProgress, PlatformCapabilityIds.DragOut,
    ];

    /// <summary>Creates services that report every operation and capability unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>
    /// Dialogs return <see cref="PickerResult{T}.Unavailable"/> and the clipboard, launcher,
    /// <see cref="IWindowPlatformServices.Monitors"/>, <see cref="IWindowPlatformServices.MessageBoxes"/>, <see cref="IWindowPlatformServices.AnnounceAsync"/>,
    /// <see cref="IWindowPlatformServices.ShowContextMenuAsync"/>, <see cref="IWindowPlatformServices.SetBadgeAsync"/>,
    /// <see cref="IWindowPlatformServices.SetProgressAsync"/> and <see cref="IWindowPlatformServices.StartDragAsync"/>
    /// <see cref="PlatformResult{T}.Unavailable"/>; the clipboard is an <see cref="IDesktopClipboard"/>, whose watch
    /// reports the reason once and ends. Nothing throws except for invalid arguments and caller cancellation.
    /// </remarks>
    public static IWindowPlatformServices Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableWindowPlatformServices(reason);
    }

    // The argument checks of IWindowPlatformServices.AnnounceAsync, shared by its implementations.
    internal static void CheckAnnouncement(string message, PlatformAnnouncementPriority priority, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        cancellationToken.ThrowIfCancellationRequested();
    }

    // The argument checks of IWindowPlatformServices.ShowContextMenuAsync, shared by its implementations.
    internal static void CheckContextMenu(ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions? options,
        CancellationToken cancellationToken)
    {
        DesktopMenuRules.ContextMenu(items);
        if (options is not null)
        {
            if (options.X.HasValue != options.Y.HasValue)
                throw new ArgumentException("Set both X and Y of the context menu position, or neither.", nameof(options));
            if (options.X is { } x && !double.IsFinite(x) || options.Y is { } y && !double.IsFinite(y))
                throw new ArgumentException("The context menu position must be finite numbers.", nameof(options));
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class UnavailableWindowPlatformServices(PlatformUnavailableReason reason)
        : IWindowPlatformServices, IFileDialogs, IDesktopClipboard, IDesktopFileLauncher
    {
        private readonly CapabilitySnapshot _snapshot = new(Guid.Empty,
            CapabilityIds.ToImmutableDictionary(static id => id, CapabilityStatus (_) => new CapabilityStatus.Unavailable(reason)));

        public IFileDialogs FileDialogs => this;
        public ITextClipboard Clipboard => this;
        public IDesktopFileLauncher FileLauncher => this;
        public IDesktopMonitors Monitors { get; } = DesktopMonitors.Unavailable(reason);
        public IDesktopMessageBoxes MessageBoxes { get; } = DesktopMessageBoxes.Unavailable(reason);
        public CapabilitySnapshot GetSnapshot() => _snapshot;

        public ValueTask<PickerResult<IDirectoryLease>> OpenDirectoryAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Pick<IDirectoryLease>(cancellationToken);
        }

        public ValueTask<PickerResult<IDirectoryLeaseList>> OpenDirectoriesAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Pick<IDirectoryLeaseList>(cancellationToken);
        }

        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Pick<IReadFileLease>(cancellationToken);
        }

        public ValueTask<PickerResult<IReadFileLeaseList>> OpenFilesAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Pick<IReadFileLeaseList>(cancellationToken);
        }

        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Pick<ISaveFileLease>(cancellationToken);
        }

        public ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
            return Result<string?>(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(text);
            return Result<PlatformUnit>(cancellationToken);
        }

        public ValueTask<PlatformResult<DesktopClipboardFormats>> GetFormatsAsync(CancellationToken cancellationToken = default) =>
            Result<DesktopClipboardFormats>(cancellationToken);

        public ValueTask<PlatformResult<string?>> ReadHtmlAsync(int maximumCharacters, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
            return Result<string?>(cancellationToken);
        }

        public ValueTask<PlatformResult<string?>> ReadRtfAsync(int maximumCharacters, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
            return Result<string?>(cancellationToken);
        }

        public ValueTask<PlatformResult<byte[]?>> ReadImageAsync(int maximumBytes, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
            return Result<byte[]?>(cancellationToken);
        }

        public ValueTask<PlatformResult<ImmutableArray<string>>> ReadFilesAsync(int maximumCount, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
            return Result<ImmutableArray<string>>(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> WriteAsync(DesktopClipboardContent content, CancellationToken cancellationToken = default)
        {
            DesktopClipboardRules.Check(content);
            return Result<PlatformUnit>(cancellationToken);
        }

        public async IAsyncEnumerable<PlatformResult<DesktopClipboardFormats>> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await Result<DesktopClipboardFormats>(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<PlatformResult<PlatformUnit>> SetBadgeAsync(DesktopBadge? badge, CancellationToken cancellationToken = default)
        {
            DesktopTaskbarRules.Check(badge, cancellationToken);
            return Result<PlatformUnit>(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> SetProgressAsync(DesktopProgress? progress, CancellationToken cancellationToken = default)
        {
            DesktopTaskbarRules.Check(progress, cancellationToken);
            return Result<PlatformUnit>(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
            return Result<PlatformUnit>(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            DesktopUriPolicy.Require(uri);
            return Result<PlatformUnit>(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> AnnounceAsync(string message,
            PlatformAnnouncementPriority priority = PlatformAnnouncementPriority.Polite, CancellationToken cancellationToken = default)
        {
            CheckAnnouncement(message, priority, cancellationToken);
            return Result<PlatformUnit>(cancellationToken);
        }

        public ValueTask<PlatformResult<DesktopMenuItem?>> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items,
            DesktopContextMenuOptions? options = null, CancellationToken cancellationToken = default)
        {
            CheckContextMenu(items, options, cancellationToken);
            return Result<DesktopMenuItem?>(cancellationToken);
        }

        public ValueTask<PlatformResult<DesktopDragEffects>> StartDragAsync(DesktopDragData data, CancellationToken cancellationToken = default)
        {
            DesktopDragRules.Check(data);
            return Result<DesktopDragEffects>(cancellationToken);
        }

        private ValueTask<PickerResult<T>> Pick<T>(CancellationToken cancellationToken) where T : IAsyncDisposable
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PickerResult<T>>(new PickerResult<T>.Unavailable(reason));
        }

        private ValueTask<PlatformResult<T>> Result<T>(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<T>>(new PlatformResult<T>.Unavailable(reason));
        }
    }
}
