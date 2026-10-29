using System.Collections.Immutable;
using Runic.Platform;

namespace Runic.Desktop;

/// <summary>Creates an embedded platform WebView host for a Runic Desktop window.</summary>
internal interface IWebUiEmbeddedHostFactory
{
    /// <summary>Gets whether the required platform WebView runtime is available.</summary>
    bool IsSupported { get; }

    /// <summary>Creates a new, initially hidden embedded host.</summary>
    IWebUiEmbeddedHost Create();
}

/// <summary>Hosts one Runic Desktop page in a platform-native embedded WebView window.</summary>
internal interface IWebUiEmbeddedHost : IAsyncDisposable
{
    /// <summary>Whether user close requests invoke the configured CloseRequested callback instead of closing.</summary>
    bool SupportsCloseConfirmation => false;
    DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.Focus |
        DesktopWindowCapabilities.Minimize | DesktopWindowCapabilities.Maximize |
        DesktopWindowCapabilities.Resize | DesktopWindowCapabilities.Move;

    /// <summary>Whether the host runs <see cref="WebUiEmbeddedHostOptions.DocumentStartScript"/> in every document.</summary>
    bool SupportsDocumentStartScript => false;

    bool SupportsNativeDispatch => false;
    bool CheckNativeAccess() => false;
    ValueTask InvokeNativeAsync(Action action, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("This host does not expose native dispatch."));

    /// <summary>Occurs after the platform window has closed.</summary>
    event EventHandler? Closed;

    /// <summary>The commands of the window's menu items: the window's target, then the application's (MenuCommandChain).</summary>
    IDesktopMenuCommandTarget? MenuCommands
    {
        get => null;
        set { }
    }

    /// <summary>Where the window shows its menu bar now.</summary>
    DesktopMenuBarPlacement MenuBarPlacement => DesktopMenuBarPlacement.None;

    /// <summary>Occurs after <see cref="MenuBarPlacement"/> has changed, possibly on the window's owning thread.</summary>
    event EventHandler? MenuBarPlacementChanged
    {
        add { }
        remove { }
    }

    /// <summary>Replaces the window's menus; called after the window opens and after every DesktopHost.SetMenuBar.</summary>
    ValueTask SetMenuBarAsync(IReadOnlyList<DesktopMenu> menus, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>Starts a native drag of checked data while the button is held; requires <see cref="DesktopWindowCapabilities.FileDrag"/>.</summary>
    ValueTask<DesktopDragEffects> StartDragAsync(DesktopDragData data, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<DesktopDragEffects>(new NotSupportedException("This host starts no drags."));

    /// <summary>Shows a checked context menu; requires <see cref="DesktopWindowCapabilities.ContextMenu"/>.</summary>
    ValueTask<DesktopMenuItem?> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<DesktopMenuItem?>(new NotSupportedException("This host shows no context menus."));

    /// <summary>Gets the last reported bounds and state, kept after the window closes; null when the host reports none.</summary>
    DesktopWindowState? State => null;

    /// <summary>Occurs once for each drop of files onto the window, on any thread; requires <see cref="DesktopWindowCapabilities.FileDrop"/>.</summary>
    event EventHandler<DesktopFileDropEventArgs>? FilesDropped
    {
        add { }
        remove { }
    }

    /// <summary>Occurs after <see cref="State"/> has changed, possibly on the window's owning thread.</summary>
    event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    /// <summary>Gets whether the platform window is open.</summary>
    bool IsOpen { get; }

    /// <summary>Gets the platform-native top-level window handle, or zero when unavailable.</summary>
    nint NativeHandle { get; }

    /// <summary>Creates the platform window and navigates its WebView.</summary>
    ValueTask ShowAsync(Uri url, WebUiEmbeddedHostOptions options, CancellationToken cancellationToken = default);

    /// <summary>Navigates the existing WebView.</summary>
    ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default);

    /// <summary>Closes the platform window.</summary>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>Activates the platform window.</summary>
    ValueTask FocusAsync(CancellationToken cancellationToken = default);

    /// <summary>Minimizes the platform window.</summary>
    ValueTask MinimizeAsync(CancellationToken cancellationToken = default);

    /// <summary>Toggles maximized and restored state.</summary>
    ValueTask MaximizeAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the outer platform-window dimensions.</summary>
    ValueTask SetSizeAsync(uint width, uint height, CancellationToken cancellationToken = default);

    /// <summary>Changes the platform-window position.</summary>
    ValueTask SetPositionAsync(int x, int y, CancellationToken cancellationToken = default);

    /// <summary>Shows or hides the platform window without destroying it.</summary>
    ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default);

    /// <summary>Starts a native move operation for a frameless window.</summary>
    ValueTask BeginMoveAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the screen reader to announce a message; requires <see cref="DesktopWindowCapabilities.Announcements"/>.</summary>
    ValueTask AnnounceAsync(string message, DesktopAnnouncementPriority priority, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("This host cannot make announcements."));

    /// <summary>Enters or leaves full screen; requires <see cref="DesktopWindowCapabilities.FullScreen"/>.</summary>
    ValueTask SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("This host cannot enter full screen."));

    /// <summary>Moves the window onto a monitor; requires <see cref="DesktopWindowCapabilities.MonitorPlacement"/>.</summary>
    ValueTask MoveToMonitorAsync(string monitorId, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("This host cannot place its window on a monitor."));

    /// <summary>Applies DesktopHost.SetSpellCheckingLanguagesAsync; a host whose checker has no languages ignores it.</summary>
    ValueTask SetSpellCheckingLanguagesAsync(IReadOnlyList<string> languages, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

/// <summary>Describes the initial state of an embedded platform WebView window.</summary>
internal sealed record WebUiEmbeddedHostOptions
{
    /// <summary>When set, suppress user close requests and invoke this callback. CloseAsync must bypass it.</summary>
    public Action? CloseRequested { get; init; }

    public uint Width { get; init; } = 800;

    public uint Height { get; init; } = 600;

    public uint? MinimumWidth { get; init; }

    public uint? MinimumHeight { get; init; }

    public int? X { get; init; }

    public int? Y { get; init; }

    public bool Centered { get; init; }

    /// <summary>The saved placement to fit to the current monitors and restore; X, Y, Width and Height hold its values.</summary>
    public DesktopWindowPlacement? Placement { get; init; }

    public bool Resizable { get; init; } = true;

    public bool Frameless { get; init; }

    public bool Transparent { get; init; }

    public bool Hidden { get; init; }

    public bool Kiosk { get; init; }

    public bool HighContrast { get; init; }

    public string? IconFile { get; init; }

    public string? ProfilePath { get; init; }

    public string? BrowserArguments { get; init; }

    public DesktopPermissionGrant AllowedPermissions { get; init; }

    /// <summary>The window's web content policy, with the Development default resolved.</summary>
    public DesktopWebContentPolicy WebContent { get; init; } = DesktopWebContentPolicy.Default with { DevTools = false };

    /// <summary>The surface's origin and additional origins, which are the application's own besides the presented URL's.</summary>
    public IReadOnlyList<Uri> AdditionalOrigins { get; init; } = [];

    /// <summary>Credential-bearing script run in every frame before page scripts, when the host supports it.</summary>
    public string? DocumentStartScript { get; init; }

    /// <summary>Reports an event of the open window, such as a terminated web process, to the host's sink and logger.</summary>
    public Action<DesktopDiagnostic>? ReportDiagnostic { get; init; }

    /// <summary>
    /// Records what the WebView did before its page connected (the controller's creation, the first navigation's start
    /// and end, a failed process) in the Bridge handshake log, which a connection timeout reports (I-176).
    /// </summary>
    public Action<string>? StartupProgress { get; init; }

    /// <summary>Where the window shows the menu bar (DesktopWindowOptions.MenuBarPresentation).</summary>
    public DesktopMenuBarPresentation MenuBarPresentation { get; init; }

    /// <summary>The window's light or dark appearance (DesktopWindowOptions.ColorScheme).</summary>
    public DesktopColorScheme ColorScheme { get; init; }

    /// <summary>Whether a Native bar may go into the Linux desktop's global menu (LinuxDesktopOptions.GlobalMenu).</summary>
    public bool GlobalMenu { get; init; } = true;

    /// <summary>The monitor to open centered on (DesktopWindowOptions.MonitorId), replacing X, Y and Centered; null otherwise.</summary>
    public string? MonitorId { get; init; }

    /// <summary>The direction of the window's native parts (DesktopWindowOptions.LayoutDirection).</summary>
    public DesktopLayoutDirection LayoutDirection { get; init; }

    /// <summary>The web view's language (DesktopWindowOptions.Language), or null for the platform's.</summary>
    public string? Language { get; init; }

    /// <summary>The macOS title bar (DesktopWindowOptions.MacOS); other hosts ignore it.</summary>
    public MacOSWindowOptions MacOS { get; init; } = new();

    /// <summary>Whether the page's fields are spell-checked (DesktopWindowOptions.SpellChecking); off is in <see cref="DocumentStartScript"/>.</summary>
    public bool SpellChecking { get; init; } = true;

    /// <summary>The application's spell-checking languages (DesktopHost.SpellCheckingLanguages); empty for the user's.</summary>
    public IReadOnlyList<string> SpellCheckingLanguages { get; init; } = [];
}
