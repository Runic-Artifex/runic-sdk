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

    bool SupportsNativeDispatch => false;
    bool CheckNativeAccess() => false;
    ValueTask DispatchNativeAsync(Action action, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("This host does not expose native dispatch."));

    /// <summary>Occurs after the platform window has closed.</summary>
    event EventHandler? Closed;

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
    ValueTask SetPositionAsync(uint x, uint y, CancellationToken cancellationToken = default);

    /// <summary>Shows or hides the platform window without destroying it.</summary>
    ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default);

    /// <summary>Starts a native move operation for a frameless window.</summary>
    ValueTask BeginMoveAsync(CancellationToken cancellationToken = default);
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

    public uint? X { get; init; }

    public uint? Y { get; init; }

    public bool Centered { get; init; }

    public bool Resizable { get; init; } = true;

    public bool Frameless { get; init; }

    public bool Transparent { get; init; }

    public bool Hidden { get; init; }

    public bool Kiosk { get; init; }

    public bool HighContrast { get; init; }

    public string? IconFile { get; init; }

    public string? ProfilePath { get; init; }

    public string? CustomParameters { get; init; }

    public DesktopPermissionGrant AllowedPermissions { get; init; }
}
