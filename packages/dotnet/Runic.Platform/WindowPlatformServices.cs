using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>Native services owned by one application window.</summary>
/// <remarks>
/// A host registers one instance per window scope, so a ViewModel can take this interface, or
/// <see cref="IFileDialogs"/>, <see cref="ITextClipboard"/> or <see cref="IDesktopFileLauncher"/> directly, in its
/// constructor. The services follow the window: before it opens they report
/// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>, and once it closes
/// <see cref="PlatformUnavailableReason.OwnerClosed"/>. A host without a native owner reports every service
/// unavailable instead of failing to resolve it.
/// </remarks>
public interface IWindowPlatformServices : IPlatformCapabilities
{
    /// <summary>File and directory dialogs owned by the window.</summary>
    IFileDialogs FileDialogs { get; }
    /// <summary>The Unicode text clipboard, used through the window's native thread.</summary>
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

    /// <summary>The desktop's monitors, in the units of the window's position and size (<see cref="PlatformCapabilityIds.Monitors"/>).</summary>
    /// <remarks>
    /// The monitors of the window's host; the list does not depend on the window. An implementation that does not
    /// override this member reports <see cref="PlatformUnavailableReason.OwnerUnavailable"/>.
    /// </remarks>
    IDesktopMonitors Monitors => DesktopMonitors.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
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
        PlatformCapabilityIds.DirectoriesOpenMultiple, PlatformCapabilityIds.Monitors,
    ];

    /// <summary>Creates services that report every operation and capability unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>
    /// Dialogs return <see cref="PickerResult{T}.Unavailable"/> and the clipboard, launcher,
    /// <see cref="IWindowPlatformServices.Monitors"/>, <see cref="IWindowPlatformServices.AnnounceAsync"/> and
    /// <see cref="IWindowPlatformServices.ShowContextMenuAsync"/>
    /// <see cref="PlatformResult{T}.Unavailable"/>; nothing throws
    /// except for invalid arguments and caller cancellation.
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
        : IWindowPlatformServices, IFileDialogs, ITextClipboard, IDesktopFileLauncher
    {
        private readonly CapabilitySnapshot _snapshot = new(Guid.Empty,
            CapabilityIds.ToImmutableDictionary(static id => id, CapabilityStatus (_) => new CapabilityStatus.Unavailable(reason)));

        public IFileDialogs FileDialogs => this;
        public ITextClipboard Clipboard => this;
        public IDesktopFileLauncher FileLauncher => this;
        public IDesktopMonitors Monitors { get; } = DesktopMonitors.Unavailable(reason);
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
