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

    /// <summary>The menu bar's command target while this window is key; only the macOS host has a menu bar.</summary>
    IDesktopMenuCommandTarget? MenuCommands
    {
        get => null;
        set { }
    }

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
}
