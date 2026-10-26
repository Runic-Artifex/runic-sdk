using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;

using Gtk;
using Runic.Desktop.Internal;
using WebKit;

namespace Runic.Desktop.Gtk4;

// Runs one GTK 4 and WebKitGTK 6 application on the Linux process main thread. Applications start it through
// DesktopEventLoop.Run(options.WithGtk4(), ...), which runs Gtk4WindowHostFactory.RunEventLoop; the native smoke
// tests call Run directly.
[SupportedOSPlatform("linux")]
internal static partial class Gtk4Application
{
    private static readonly object Gate = new();
    private static readonly HashSet<Gtk4WindowHost> Hosts = [];
    private static int _state;
    // 0: not probed, 1: a display opened, 2: no display could be opened.
    private static int _display;

    /// <summary>Runs an application's asynchronous workload while GTK owns the calling main thread.</summary>
    /// <remarks>
    /// Call this directly from <c>Main</c>, before top-level awaits. The GTK 4
    /// provider does not support re-entering or restarting its native runtime.
    /// </remarks>
    public static int Run(Func<Task<int>> application) => RunCore(application, null, "Gtk4Application.Run");

    /// <summary>Runs GTK with the application's installed reverse-DNS identity.</summary>
    /// <remarks>Inside Flatpak this must match the package ID. The overload without an ID uses the Flatpak ID automatically.</remarks>
    public static int Run(Func<Task<int>> application, string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        return RunCore(application, applicationId, "Gtk4Application.Run");
    }

    // entryPoint names the API the application called, for the error messages.
    internal static int RunCore(Func<Task<int>> application, string? applicationId, string entryPoint)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("GTK 4 and WebKitGTK 6 are available only on Linux.");
        }
        if (!IsMainThread)
        {
            throw new InvalidOperationException($"Call {entryPoint} directly from the Linux process main thread, before awaiting application work.");
        }
        if (!Gtk4Runtime.IsAvailable)
        {
            throw new PlatformNotSupportedException("GTK 4.12 and WebKitGTK 6 are required for the GTK 4 embedded presentation.");
        }
        string? flatpakId = File.Exists("/.flatpak-info") ? Environment.GetEnvironmentVariable("FLATPAK_ID") : null;
        if (!string.IsNullOrEmpty(flatpakId) && applicationId is not null && applicationId != flatpakId)
            throw new ArgumentException("The GTK application ID must match the Flatpak package ID.", nameof(applicationId));
        applicationId ??= string.IsNullOrEmpty(flatpakId) ? "dev.runic.desktop.gtk4" : flatpakId;
        if (!Gio.Application.IdIsValid(applicationId))
            throw new ArgumentException("A valid reverse-DNS GTK application ID is required.", nameof(applicationId));
        lock (Gate)
        {
            if (_state != 0)
            {
                throw new InvalidOperationException($"{entryPoint} supports one non-reentrant GTK 4 application lifetime per process.");
            }
            _state = 1;
        }

        LinuxDesktopRuntime.ClaimBackend(LinuxEmbeddedBackend.Gtk4WebKit6);
        if (!TryOpenDisplay())
        {
            lock (Gate) _state = 3;
            throw new PlatformNotSupportedException(NoDisplayMessage);
        }
        var nativeApplication = Gtk.Application.New(applicationId, Gio.ApplicationFlags.NonUnique);
        nativeApplication.Hold();
        Exception? failure = null;
        var exitCode = 1;
        Task? work = null;
        nativeApplication.OnActivate += OnActivate;
        try
        {
            nativeApplication.RunWithSynchronizationContext(null);
            work?.GetAwaiter().GetResult();
        }
        finally
        {
            nativeApplication.OnActivate -= OnActivate;
            Gtk4Dispatcher.Instance.Detach(nativeApplication);
            nativeApplication.Dispose();
            lock (Gate)
            {
                _state = 3;
            }
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return exitCode;

        void OnActivate(Gio.Application _, EventArgs __)
        {
            if (work is not null)
            {
                return;
            }
            Gtk4Dispatcher.Instance.Attach(nativeApplication, SynchronizationContext.Current
                ?? throw new InvalidOperationException("GTK did not install a synchronization context."));
            work = Task.Run(async () =>
            {
                try
                {
                    exitCode = await application().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    failure = error;
                }
                finally
                {
                    BeginStopping();
                    try
                    {
                        await CloseLiveHostsAsync().ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        failure ??= error;
                    }
                    finally
                    {
                        Gtk4Dispatcher.Instance.Post(nativeApplication.Quit);
                    }
                }
            });
        }
    }

    internal static bool IsRunning => Volatile.Read(ref _state) == 1;

    internal static bool IsMainThread => GetThreadId() == GetProcessId();

    internal static bool DisplayUnavailable => Volatile.Read(ref _display) == 2;

    internal const string NoDisplayMessage = "GTK 4 could not open a display.";

    // gtk_init exits the process when no display can be opened; gtk_init_check reports the failure instead.
    // GirCore's Gtk.Module.Initialize calls gtk_init, so probe through the C entry point before GirCore runs;
    // after a successful check, gtk_init is a no-op. Call on the process main thread before GTK starts; the
    // result is kept for the process lifetime.
    internal static bool TryOpenDisplay()
    {
        var state = Volatile.Read(ref _display);
        if (state != 0) return state == 1;
        var opened = GtkInitCheck() != 0;
        if (opened) Gtk4X11Placement.DisplayOpened();
        Volatile.Write(ref _display, opened ? 1 : 2);
        return opened;
    }

    internal static void Register(Gtk4WindowHost host)
    {
        lock (Gate)
        {
            if (_state != 1)
            {
                throw new InvalidOperationException("GTK 4 is stopping; open windows only inside DesktopEventLoop.Run(options.WithGtk4(), application).");
            }
            Hosts.Add(host);
        }
    }

    internal static void Unregister(Gtk4WindowHost host)
    {
        lock (Gate)
        {
            Hosts.Remove(host);
        }
    }

    private static void BeginStopping()
    {
        lock (Gate)
        {
            if (_state == 1)
            {
                _state = 2;
            }
        }
    }

    private static async Task CloseLiveHostsAsync()
    {
        Gtk4WindowHost[] hosts;
        lock (Gate)
        {
            hosts = Hosts.ToArray();
        }
        Exception? failure = null;
        foreach (var host in hosts)
        {
            try
            {
                await host.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure ??= error;
            }
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_init_check")]
    private static partial int GtkInitCheck();
    [LibraryImport("libc", EntryPoint = "getpid")]
    private static partial int GetProcessId();
    [LibraryImport("libc", EntryPoint = "gettid")]
    private static partial int GetThreadId();
}

/// <summary>Creates explicitly selected GTK 4 and WebKitGTK 6 window hosts.</summary>
/// <remarks>
/// Select it with <see cref="Gtk4DesktopHostOptionsExtensions.WithGtk4(DesktopHostOptions)"/>, which also selects
/// <see cref="LinuxEmbeddedBackend.Gtk4WebKit6"/>, or set it as <see cref="DesktopHostOptions.WindowHostFactory"/>
/// together with that backend.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class Gtk4WindowHostFactory : ILinuxDesktopWindowHostFactory, IDesktopEventLoopWindowHostFactory
{
    private readonly string? _applicationId;

    /// <inheritdoc />
    public LinuxEmbeddedBackend Backend => LinuxEmbeddedBackend.Gtk4WebKit6;

    /// <summary>Gets the reverse-DNS GTK application ID <see cref="RunEventLoop"/> uses, or <see langword="null"/> for the default.</summary>
    /// <remarks>Inside Flatpak the default is the package ID, and an explicit ID must match it.</remarks>
    public string? ApplicationId
    {
        get => _applicationId;
        init
        {
            if (value is not null) Gtk4DesktopHostOptionsExtensions.ValidateApplicationId(value);
            _applicationId = value;
        }
    }

    /// <inheritdoc />
    /// <remarks>Runs the one GTK 4 application lifetime of the process with <see cref="ApplicationId"/>.</remarks>
    public int RunEventLoop(Func<Task<int>> application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("GTK 4 and WebKitGTK 6 are available only on Linux.");
        }
        return Gtk4Application.RunCore(application, ApplicationId, "DesktopEventLoop.Run(options.WithGtk4(), application)");
    }

    /// <inheritdoc />
    /// <remarks>
    /// Opens the GTK display with <c>gtk_init_check</c> on the main thread, because <c>gtk_init</c> would end the
    /// process when no display is available (an SSH session, a headless machine, or WSL without WSLg). When no
    /// display opens it returns <c>gtk4-display-unavailable</c>, and the factory is unsupported from then on.
    /// </remarks>
    public IReadOnlyList<DesktopDiagnostic> PrepareEventLoop()
    {
        if (!OperatingSystem.IsLinux() || !Gtk4Application.IsMainThread)
        {
            // RunEventLoop reports these with the entry point the application called.
            return [];
        }
        LinuxDesktopRuntime.ClaimBackend(Backend);
        return Gtk4Application.TryOpenDisplay() ? [] : [DisplayUnavailable()];
    }

    private static DesktopDiagnostic DisplayUnavailable() => Gtk4Runtime.Missing(
        "gtk4-display-unavailable",
        Gtk4Application.NoDisplayMessage,
        "Run inside a graphical session with DISPLAY or WAYLAND_DISPLAY set (on WSL, enable WSLg), or use an installed browser.");

    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsLinux() &&
        LinuxDesktopRuntime.CanUse(Backend) && Gtk4Runtime.IsAvailable && !Gtk4Application.DisplayUnavailable;

    /// <inheritdoc />
    /// <remarks>
    /// An open window reports <see cref="DesktopWindowCapabilities.Announcements"/> only with GTK 4.14 or later, and
    /// <see cref="DesktopWindowCapabilities.Move"/> and <see cref="DesktopWindowCapabilities.MonitorPlacement"/> only on X11.
    /// </remarks>
    public DesktopWindowCapabilities Capabilities => Gtk4WindowHost.SupportedCapabilities | DesktopWindowCapabilities.Announcements |
        Gtk4WindowHost.PositionCapabilities;

    /// <inheritdoc />
    /// <remarks>
    /// GTK 4 windows reject Centered, Transparent, HighContrast, ProfilePath, BrowserArguments and IconFile, and X and
    /// Y except on X11. They ignore ColorScheme (<c>color-scheme-unsupported</c>): WebKitGTK follows the GTK theme for
    /// every window.
    /// </remarks>
    public IReadOnlyList<DesktopDiagnostic> ValidateOptions(DesktopWindowHostOptions options) =>
        [.. Gtk4WindowHost.GetUnsupportedOptions(options), .. Gtk4WindowHost.GetIgnoredOptions(options)];

    // Explicit, so the factory gains no public member: DesktopHost.Monitors returns these.
    Runic.Platform.IDesktopMonitors IDesktopWindowHostFactory.Monitors => Gtk4Monitors.Source;

    /// <inheritdoc />
    /// <remarks>Reports <c>linux-embedded-backend-conflict</c>, <c>gtk4-runtime-missing</c> and <c>webkitgtk6-runtime-missing</c> together.</remarks>
    public IReadOnlyList<DesktopDiagnostic> GetAvailabilityDiagnostics()
    {
        if (!OperatingSystem.IsLinux())
        {
            return [Gtk4Runtime.Missing("embedded-platform-unsupported", "GTK 4 and WebKitGTK 6 are available only on Linux.",
                "Configure Gtk4WindowHostFactory only on Linux; DesktopHostOptions.WithGtk4() does this automatically.")];
        }
        List<DesktopDiagnostic> diagnostics = [];
        if (!LinuxDesktopRuntime.CanUse(Backend))
        {
            diagnostics.Add(Gtk4Runtime.Missing("linux-embedded-backend-conflict", "Another Linux toolkit is already initialized.",
                "Start a new process to select a different toolkit."));
        }
        diagnostics.AddRange(Gtk4Runtime.GetMissingLibraries());
        if (Gtk4Application.DisplayUnavailable) diagnostics.Add(DisplayUnavailable());
        return diagnostics;
    }

    /// <inheritdoc />
    public IDesktopWindowHost Create()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("GTK 4 and WebKitGTK 6 are available only on Linux.");
        }

        if (!Gtk4Runtime.IsAvailable)
        {
            throw new PlatformNotSupportedException(
                "GTK 4.12 and WebKitGTK 6 are required for the GTK 4 embedded presentation.");
        }
        if (!Gtk4Application.IsRunning)
        {
            throw new InvalidOperationException("Start GTK 4 through DesktopEventLoop.Run(options.WithGtk4(), application) before creating a Gtk4WindowHostFactory window host.");
        }

        return new Gtk4WindowHost();
    }
}

// gtk_accessible_announce, new in GTK 4.14; the provider supports GTK 4.12.
[SupportedOSPlatform("linux")]
internal static unsafe class Gtk4Announcement
{
    private static readonly Lazy<nint> Function = new(static () =>
        NativeLibrary.TryLoad(Gtk4NativeLibraries.Gtk, out var library)
        && NativeLibrary.TryGetExport(library, "gtk_accessible_announce", out var function) ? function : 0);

    internal static bool IsAvailable => Function.Value != 0;

    // GtkAccessibleAnnouncementPriority: LOW = 0, MEDIUM = 1, HIGH = 2. GTK sends MEDIUM as polite and HIGH as assertive.
    internal static void Announce(nint accessible, string message, bool assertive)
    {
        var text = Marshal.StringToCoTaskMemUTF8(message);
        try
        {
            ((delegate* unmanaged[Cdecl]<nint, nint, int, void>)Function.Value)(accessible, text, assertive ? 2 : 1);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }
}

internal static class Gtk4Runtime
{
    private static readonly string[] GtkNames = [Gtk4NativeLibraries.Gtk];
    private static readonly string[] WebKitNames = Gtk4NativeLibraries.WebKit;

    internal static bool IsAvailable => CanFind(GtkNames) && CanFind(WebKitNames);

    // Uses the same codes as DesktopPlatform's GTK4 checks so tools can match them.
    internal static IEnumerable<DesktopDiagnostic> GetMissingLibraries()
    {
        if (!CanFind(GtkNames))
        {
            yield return Missing("gtk4-runtime-missing", "The GTK 4 library libgtk-4.so.1 was not discovered.",
                "Install GTK 4.12 or newer (for example libgtk-4-1) in the native loader search path.");
        }
        if (!CanFind(WebKitNames))
        {
            yield return Missing("webkitgtk6-runtime-missing", "The WebKitGTK 6.0 library libwebkitgtk-6.0.so.4 was not discovered.",
                "Install WebKitGTK 6.0 (for example libwebkitgtk-6.0-4) in the native loader search path.");
        }
    }

    internal static DesktopDiagnostic Missing(string code, string message, string remediation) =>
        new(DesktopErrorCategory.Unavailable, code, message, Retryable: false, CorrelationId: string.Empty, Remediation: remediation);

    private static bool CanFind(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (LinuxDesktopRuntime.IsLibraryAvailable(name))
            {
                return true;
            }
        }
        return false;
    }
}

[SupportedOSPlatform("linux")]
internal sealed partial class Gtk4WindowHost : IDesktopNativeDispatchWindowHost, IDesktopWindowStateHost, IDesktopFileDropWindowHost,
    IDesktopPlacementWindowHost
{
    internal const DesktopWindowCapabilities SupportedCapabilities = DesktopWindowCapabilities.NativeHandle |
        DesktopWindowCapabilities.Focus | DesktopWindowCapabilities.Minimize |
        DesktopWindowCapabilities.Maximize | DesktopWindowCapabilities.Resize |
        DesktopWindowCapabilities.CloseConfirmation | DesktopWindowCapabilities.State | DesktopWindowCapabilities.FileDrop |
        DesktopWindowCapabilities.FullScreen | DesktopWindowCapabilities.MenuBar | DesktopWindowCapabilities.ContextMenu |
        DesktopWindowCapabilities.FileDrag;
    private DesktopWindowState? _state;
    // GTK thread only: the X11 position of the window while neither maximized nor full-screen, a position to apply
    // when a hidden window is first shown, and the X event connection that reports moves.
    private (int X, int Y)? _normalPosition;
    private (int X, int Y)? _pendingPosition;
    private (nint Display, nuint Handler) _moveWatch;
    private FileDropPairing? _drops;
    private bool _receivesFileDrops;
    private int _stateUpdateQueued;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Gtk.ApplicationWindow? _window;
    private WebView? _webView;
    private DesktopWindowHostOptions? _options;
    private Uri? _presentedUrl;
    // With the MediaCapture grant only: the page's subframes, which the grant depends on.
    private WebKitGtkMediaFrames? _mediaFrames;
    private readonly WebKitGtkWebProcess _webProcess = new();
    private int _isOpen;
    private int _disposed;
    private int _forceClose;
    private int _dispatcherLease;
    private int _cleanupQueued;
    private int _createStarted;
    private NativeObjectFinalizationProbe? _finalization;
    // GTK thread only: the window's close-request and destroy handlers (WindowLifetimeSignals).
    private (nuint CloseRequest, nuint Destroy) _windowSignals;
    private Gtk.EventControllerKey? _keys;
    private UserContentManager? _contentManager;
    // GTK thread only: the window's DesktopWebContentPolicy (SEC-04) and the network session's download-started
    // connection. The session is shared by the process's views and outlives this one.
    private WebKitGtkWebContent? _webContent;
    private nint _downloadSession;
    private ulong _downloadHandler;

    // Tests observe the URLs handed to the system's handler here instead of launching it.
    internal static Action<string>? ExternalOpenerOverride { get; set; }

    public event EventHandler? Closed;

    public event EventHandler? StateChanged;

    public event EventHandler<DesktopFileDropEventArgs>? FilesDropped;

    public DesktopWindowState? State => Volatile.Read(ref _state);

    public bool SupportsCloseConfirmation => true;

    public bool SupportsDocumentStartScript => true;

    // FileDrop only once WebKit's drop target was found; see Gtk4FileDrops.
    public DesktopWindowCapabilities Capabilities => (_window is null || _receivesFileDrops ? SupportedCapabilities : SupportedCapabilities & ~DesktopWindowCapabilities.FileDrop) |
        (Gtk4Announcement.IsAvailable ? DesktopWindowCapabilities.Announcements : DesktopWindowCapabilities.None) | PositionCapabilities;

    // GTK 4 positions windows only on X11 (Gtk4X11Placement).
    internal static DesktopWindowCapabilities PositionCapabilities =>
        Gtk4X11Placement.IsActive ? DesktopWindowCapabilities.Move | DesktopWindowCapabilities.MonitorPlacement : DesktopWindowCapabilities.None;

    public bool SupportsNativeDispatch => true;

    public bool IsOpen => Volatile.Read(ref _isOpen) != 0;

    public nint NativeHandle => _window?.Handle.DangerousGetHandle() ?? 0;

    public bool CheckNativeAccess() => Gtk4Dispatcher.Instance.CheckAccess;

    public async ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Gtk4Application.IsRunning)
        {
            throw new InvalidOperationException("Open GTK 4 windows inside DesktopEventLoop.Run(options.WithGtk4(), application).");
        }
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        if (IsOpen)
        {
            await NavigateAsync(url, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!Gtk4Runtime.IsAvailable)
        {
            throw new PlatformNotSupportedException("GTK 4.12 and WebKitGTK 6 are required for the GTK 4 embedded presentation.");
        }
        LinuxDesktopRuntime.ClaimBackend(LinuxEmbeddedBackend.Gtk4WebKit6);
        _options = options;
        _presentedUrl = url;
        await Gtk4Dispatcher.Instance.AcquireAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _dispatcherLease, 1);
        try
        {
            await Gtk4Dispatcher.Instance.InvokeAsync(() => Create(url), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cancellation can withdraw the queued creation before it runs. Only a
            // started creation has native objects whose release completes _closed.
            if (Volatile.Read(ref _createStarted) != 0)
            {
                await _closed.Task.ConfigureAwait(false);
            }
            await ReleaseDispatcherAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        return new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            GetWebView().LoadUri(url.AbsoluteUri);
        }, cancellationToken));
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (IsOpen)
        {
            Volatile.Write(ref _forceClose, 1);
            try
            {
                await Gtk4Dispatcher.Instance.InvokeAsync(DestroyWindow, cancellationToken).ConfigureAwait(false);
                await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _forceClose, 0);
            }
        }
        else if (Volatile.Read(ref _cleanupQueued) != 0)
        {
            // A user close may already have passed GTK's destroy signal. Its
            // queued disposal must complete before the caller releases the
            // host's dispatcher lease.
            await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        await ReleaseDispatcherAsync().ConfigureAwait(false);
    }

    public ValueTask FocusAsync(CancellationToken cancellationToken = default) =>
        new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            Present(GetWindow());
            _ = GetWebView().GrabFocus();
        }, cancellationToken));

    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) =>
        new(Gtk4Dispatcher.Instance.InvokeAsync(() => GetWindow().Minimize(), cancellationToken));

    // WebKitGTK reports no live-region change to AT-SPI (#318); the window announces through GTK's AT-SPI context.
    public ValueTask AnnounceAsync(string message, DesktopAnnouncementPriority priority, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Gtk4Announcement.IsAvailable)
        {
            return ValueTask.FromException(new NotSupportedException("GTK 4.14 or later is required for announcements."));
        }
        return new(Gtk4Dispatcher.Instance.InvokeAsync(() => Gtk4Announcement.Announce(
            GetWindow().Handle.DangerousGetHandle(), message, priority == DesktopAnnouncementPriority.Assertive), cancellationToken));
    }

    public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) =>
        new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            var window = GetWindow();
            if (window.IsMaximized())
            {
                window.Unmaximize();
            }
            else
            {
                window.Maximize();
            }
        }, cancellationToken));

    public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return new(Gtk4Dispatcher.Instance.InvokeAsync(() => GetWindow().SetDefaultSize(width, height), cancellationToken));
    }

    public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default)
    {
        if (!Gtk4X11Placement.IsActive)
        {
            return ValueTask.FromException(new NotSupportedException("GTK 4 positions top-level windows only on X11; on Wayland the compositor places them."));
        }
        return new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            var window = GetWindow();
            if (!window.IsVisible())
            {
                // An unmapped window is placed when it is shown.
                _pendingPosition = (x, y);
                return;
            }
            if (!Gtk4X11Placement.Move(SurfaceOf(window), x, y))
            {
                throw new NotSupportedException("The GTK 4 window has no X11 surface.");
            }
            QueueStateUpdate();
        }, cancellationToken));
    }

    private static nint SurfaceOf(Gtk.Window window) => window.GetSurface()?.Handle.DangerousGetHandle() ?? 0;

    // GTK thread. Presents the window and, on X11, places a window shown for the first time at its requested position.
    // A window manager places a newly mapped window itself, so the position is requested again once it is mapped.
    private void Present(Gtk.Window window)
    {
        window.Present();
        if (_pendingPosition is not { } position)
        {
            return;
        }
        _pendingPosition = null;
        var surface = SurfaceOf(window);
        Gtk4X11Placement.Move(surface, position.X, position.Y);
        Gtk4Dispatcher.Instance.Post(() =>
        {
            try
            {
                if (_window is { } current && ReferenceEquals(current, window) && IsOpen)
                {
                    Gtk4X11Placement.Move(SurfaceOf(current), position.X, position.Y);
                    QueueStateUpdate();
                }
            }
            catch
            {
                // Never unwind through GTK.
            }
        });
    }

    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            if (visible)
            {
                Present(GetWindow());
            }
            else
            {
                GetWindow().Hide();
            }
        }, cancellationToken));

    public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("GTK 4 frameless move is not yet implemented."));

    public ValueTask InvokeNativeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!IsOpen)
        {
            return ValueTask.FromException(new InvalidOperationException("The GTK 4 window is not open."));
        }
        return new(Gtk4Dispatcher.Instance.InvokeAsync(action, cancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void Create(Uri url)
    {
        Volatile.Write(ref _createStarted, 1);
        Gtk.ApplicationWindow? window = null;
        WebView? webView = null;
        NativeObjectFinalizationProbe? finalization = null;
        Gtk.EventControllerKey? keys = null;
        UserContentManager? contentManager = null;
        var registered = false;
        try
        {
            var options = _options ?? throw new InvalidOperationException("The GTK 4 host has no window options.");
            var application = Gtk4Dispatcher.Instance.Application;
            window = Gtk.ApplicationWindow.New(application);
            // Gtk.Application initializes GTK's module graph, but WebKit is an
            // independent GirCore module and must register its native resolver
            // before WebView.New reaches a generated P/Invoke.
            WebKit.Module.Initialize();
            // Do not bind the view to WebKit's process-global default context. Its
            // process cache is released during libc shutdown, after the managed
            // window lifetime has ended. A per-view context is reference-counted by
            // the WebView and is disposed on the GTK dispatcher with that view.
            using var webContext = WebContext.New();
            // DesktopWindowOptions.Language: the page's navigator.language and Accept-Language.
            if (options.Language is { } language) webContext.SetPreferredLanguages([language]);
            using var contextValue = new GObject.Value(webContext);
            webView = WebView.NewWithProperties(
                [new GObject.ConstructArgument("web-context", contextValue)]);
            window.Title = "Runic Desktop";
            var width = options.Width;
            var height = options.Height;
            var x11 = Gtk4X11Placement.IsActive;
            (int X, int Y)? position = x11 && options.X is { } requestedX && options.Y is { } requestedY ? (requestedX, requestedY) : null;
            // DesktopWindowOptions.MonitorId: centered on its work area on X11; on Wayland only full screen uses it.
            var chosen = options.MonitorId is { } monitorId ? Gtk4Monitors.Find(monitorId) : null;
            _fullScreenMonitor = chosen?.Handle;
            if (options.Placement is not null)
            {
                if (position is { } placed)
                {
                    // X11: keep a restored window on a monitor that exists now, as GTK 3 does.
                    var fitted = WindowPlacementClamp.Clamp(new ScreenRect(placed.X, placed.Y, width, height), Gtk4X11Placement.GetWorkAreas());
                    (position, width, height) = ((fitted.X, fitted.Y), fitted.Width, fitted.Height);
                }
                else
                {
                    // Wayland: the compositor chooses the monitor, so fit the restored size to the largest one. The
                    // position of a placement is ignored.
                    (width, height) = chosen is { } target
                        ? WindowPlacementClamp.ClampSize(width, height, ToScreenRect(target.Monitor.WorkArea))
                        : FitToLargestMonitor(width, height);
                }
            }
            if (chosen is { } onMonitor && x11 && position is null)
            {
                var area = CenterIn(width, height, ToScreenRect(onMonitor.Monitor.WorkArea));
                (position, width, height) = ((area.X, area.Y), area.Width, area.Height);
            }
            window.SetDefaultSize(width, height);
            window.SetResizable(options.Resizable);
            window.SetDecorated(!options.Frameless && !options.Kiosk);
            if (options.MinimumWidth is not null || options.MinimumHeight is not null)
            {
                webView.SetSizeRequest(
                    options.MinimumWidth is { } minimumWidth ? minimumWidth : -1,
                    options.MinimumHeight is { } minimumHeight ? checked((int)minimumHeight) : -1);
            }
            if (options.DocumentStartScript is { } documentStartScript)
            {
                // The script compares complete origins, including the listener port, itself.
                using var script = UserScript.New(
                    documentStartScript,
                    UserContentInjectedFrames.AllFrames,
                    UserScriptInjectionTime.Start,
                    null,
                    null);
                webView.GetUserContentManager().AddScript(script);
            }
            // When Tab leaves the page, WebKit asks the window to move focus on. GTK 4
            // would move it into the hidden text view WebKit uses to translate key
            // bindings, a child of the view from its construction, and keys would no
            // longer reach the page. Without it, focus wraps back to the view, which
            // must therefore be focusable itself (WebKit sets only can-focus). The
            // keyboard script then focuses the other end of the page. Children WebKit
            // adds later, such as script dialogs and the emoji chooser, must take focus.
            for (var child = webView.GetFirstChild(); child is not null; child = child.GetNextSibling())
            {
                if (child.GetCssName() == "textview")
                {
                    child.SetCanFocus(false);
                }
            }
            webView.SetFocusable(true);
            contentManager = webView.GetUserContentManager();
            if (!contentManager.RegisterScriptMessageHandler(WebKitGtkKeyboard.ZoomMessageHandler, WebKitGtkKeyboard.ScriptWorld))
            {
                throw new InvalidOperationException("WebKitGTK could not register the zoom message handler.");
            }
            UserContentManager.ScriptMessageReceivedSignal.Connect(contentManager, OnZoomMessage, false, WebKitGtkKeyboard.ZoomMessageHandler);
            if (contentManager.RegisterScriptMessageHandler(FileDropSupport.WebKitMessageHandler, WebKitGtkKeyboard.ScriptWorld))
            {
                UserContentManager.ScriptMessageReceivedSignal.Connect(contentManager, OnFileDropMessage, false, FileDropSupport.WebKitMessageHandler);
                // In every frame, so drops into same-origin frames carry their label; the handler checks the frame origin.
                using var dropScript = UserScript.NewForWorld(FileDropSupport.WebKitScript, UserContentInjectedFrames.AllFrames,
                    UserScriptInjectionTime.Start, WebKitGtkKeyboard.ScriptWorld, null, null);
                contentManager.AddScript(dropScript);
            }
            if ((options.AllowedPermissions & DesktopPermissionGrant.MediaCapture) != 0)
            {
                if (!contentManager.RegisterScriptMessageHandler(WebKitGtkMediaFrames.MessageHandler, WebKitGtkKeyboard.ScriptWorld))
                {
                    throw new InvalidOperationException("WebKitGTK could not register the media frames message handler.");
                }
                _mediaFrames = new WebKitGtkMediaFrames(url);
                UserContentManager.ScriptMessageReceivedSignal.Connect(contentManager, OnMediaFramesMessage, false, WebKitGtkMediaFrames.MessageHandler);
                using var framesScript = UserScript.NewForWorld(WebKitGtkMediaFrames.Script, UserContentInjectedFrames.AllFrames,
                    UserScriptInjectionTime.Start, WebKitGtkKeyboard.ScriptWorld, null, null);
                contentManager.AddScript(framesScript);
            }
            using (var keyboardScript = UserScript.NewForWorld(
                WebKitGtkKeyboard.Script,
                UserContentInjectedFrames.TopFrame,
                UserScriptInjectionTime.Start,
                WebKitGtkKeyboard.ScriptWorld,
                null,
                null))
            {
                contentManager.AddScript(keyboardScript);
            }
            // WebKit sends each key to the page first and passes on only those the page
            // did not handle. They reach the window's bubble phase. The keyboard script
            // reports Control and the wheel, which WebKit would otherwise scroll with.
            keys = Gtk.EventControllerKey.New();
            keys.OnKeyPressed += OnUnhandledKeyPressed;
            window.AddController(keys);
            // The Native menu bar goes above the view in a box (B10); other windows hold the view alone.
            var content = CreateMenuContent(options, webView);
            window.SetChild(content);
            // DesktopWindowOptions.LayoutDirection: the window, its header bar and the view; the page keeps its own dir.
            if (options.LayoutDirection != Runic.Platform.DesktopLayoutDirection.Default)
            {
                var direction = options.LayoutDirection == Runic.Platform.DesktopLayoutDirection.RightToLeft ? Gtk.TextDirection.Rtl : Gtk.TextDirection.Ltr;
                window.SetDirection(direction);
                content.SetDirection(direction);
                webView.SetDirection(direction);
            }
            // Hidden windows still need a native surface for portal ownership
            // and orderly Wayland teardown. Realizing does not map/show them.
            // Some GTK 4 Wayland session cleanup versions assume it exists.
            // Cast explicitly: Gtk.Native.Realize only initializes a surface
            // that already exists; Gtk.Widget.Realize creates it first.
            ((Gtk.Widget)window).Realize();
            if (position is { } initial)
            {
                // Before mapping, which is all a window manager-less X server needs; Present requests it again.
                Gtk4X11Placement.Move(SurfaceOf(window), initial.X, initial.Y);
                _pendingPosition = initial;
                _normalPosition = initial;
            }
            finalization = new NativeObjectFinalizationProbe();
            finalization.Track(window.Handle.DangerousGetHandle());
            finalization.Track(webView.Handle.DangerousGetHandle());
            _windowSignals = WindowLifetimeSignals.Connect(this, window);
            PermissionRequestSignal.Connect(this, webView);
            WebProcessTerminatedSignal.Connect(this, webView);
            TitleSignal.Connect(this, webView);
            _drops = new FileDropPairing(drop => FilesDropped?.Invoke(this, drop), FileDropSupport.LabelWait);
            _receivesFileDrops = Gtk4FileDrops.Connect(this, webView);
            ApplyWebContent(webView, new WebContentRules(options.WebContent, url, options.AdditionalOrigins));
            webView.LoadUri(url.AbsoluteUri);
            if (options.Placement is { Maximized: true } && !options.Kiosk)
            {
                // Before presenting, so the window maps maximized and keeps the restored default size.
                window.Maximize();
            }
            if (!options.Hidden)
            {
                Present(window);
            }
            if (options.Kiosk)
            {
                window.Fullscreen();
            }
            else if (options.Placement is { FullScreen: true })
            {
                // A placement saved full-screen: on the chosen monitor (also on Wayland), else where the window opened.
                EnterFullScreen(window);
            }
            WindowStateSignals.Connect(this, window);
            _moveWatch = Gtk4X11Placement.ConnectMoved(SurfaceOf(window), QueueStateUpdate);

            _window = window;
            _webView = webView;
            _finalization = finalization;
            _keys = keys;
            _contentManager = contentManager;
            Gtk4Application.Register(this);
            registered = true;
            Volatile.Write(ref _isOpen, 1);
            UpdateState();
            // The window is realized, also when hidden, so it has its X11 surface.
            StartGlobalMenu(options, window);
        }
        catch
        {
            if (registered)
            {
                Gtk4Application.Unregister(this);
            }
            DetachZoomHandlers(keys, contentManager);
            Gtk4FileDrops.Disconnect(this);
            DisconnectDownloads();
            Gtk4X11Placement.Disconnect(_moveWatch);
            _moveWatch = default;
            if (window is not null)
            {
                WindowLifetimeSignals.Disconnect(window, _windowSignals);
                _windowSignals = default;
                window.Destroy();
            }
            webView?.Dispose();
            window?.Dispose();
            _window = null;
            _webView = null;
            _keys = null;
            _contentManager = null;
            // GirCore's wrapper of the box holds a reference that would keep the view alive.
            _menuBox?.Dispose();
            _menuBox = null;
            _pointerMotion?.Dispose();
            _pointerMotion = null;
            Volatile.Write(ref _isOpen, 0);
            var tracked = Interlocked.Exchange(ref _finalization, null) ?? finalization;
            if (tracked is null)
            {
                _closed.TrySetResult();
            }
            else
            {
                _ = CompleteClosedAfterFinalizationAsync(tracked);
            }
            throw;
        }
    }

    private static void ValidateOptions(DesktopWindowHostOptions options)
    {
        var rejected = GetUnsupportedOptions(options);
        if (rejected.Count > 0)
        {
            throw new NotSupportedException(rejected[0].Message);
        }
    }

    // Window placement is the compositor's on Wayland, and GTK 4 removed global positioning.
    internal static IReadOnlyList<DesktopDiagnostic> GetUnsupportedOptions(DesktopWindowHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<DesktopDiagnostic> diagnostics = [];
        // A restored placement keeps its size and maximized state; its position is ignored, not rejected, so a
        // placement saved by another backend still opens.
        // On X11 Runic positions the window through Xlib (Gtk4X11Placement).
        var x11 = Gtk4X11Placement.IsActive;
        if (options.X is not null && options.Placement is null && !x11)
            diagnostics.Add(Unsupported(nameof(DesktopWindowOptions.X), "GTK 4 positions windows only on X11; on Wayland the compositor places every window."));
        if (options.Y is not null && options.Placement is null && !x11)
            diagnostics.Add(Unsupported(nameof(DesktopWindowOptions.Y), "GTK 4 positions windows only on X11; on Wayland the compositor places every window."));
        if (options.Centered)
            diagnostics.Add(Unsupported(nameof(DesktopWindowOptions.Centered), "GTK 4 does not support requested global window placement."));
        if (options.Transparent)
            diagnostics.Add(Unsupported(nameof(DesktopWindowOptions.Transparent), "Transparent GTK 4 WebKit windows are not implemented."));
        if (options.HighContrast)
            diagnostics.Add(Unsupported(nameof(DesktopWindowOptions.HighContrast),
                "GTK 4 high-contrast presentation is not implemented.",
                "Set HighContrast to false; when it is unset, the system high-contrast setting applies."));
        if (!string.IsNullOrWhiteSpace(options.ProfilePath))
            diagnostics.Add(Unsupported(nameof(DesktopWindowOptions.ProfilePath), "GTK 4 WebKit profile persistence is not implemented."));
        if (!string.IsNullOrWhiteSpace(options.BrowserArguments))
            diagnostics.Add(Unsupported(nameof(DesktopWindowOptions.BrowserArguments), "GTK 4 WebKit custom arguments are not supported."));
        if (!string.IsNullOrWhiteSpace(options.IconFile))
            diagnostics.Add(Unsupported(nameof(DesktopWindowOptions.IconFile), "GTK 4 window icon files are not implemented."));
        // On Wayland a chosen monitor applies only to a full-screen placement; the compositor places other windows.
        if (options.MonitorId is not null && !x11 && options.Placement is not { FullScreen: true })
            diagnostics.Add(Unsupported(nameof(DesktopWindowOptions.MonitorId),
                "GTK 4 on Wayland ignores DesktopWindowOptions.MonitorId except for a full-screen placement; the compositor places the window.",
                "Remove the option on Wayland, or rely on the compositor's placement.") with { Severity = DesktopDiagnosticSeverity.Warning });
        return diagnostics;
    }

    // Options the window opens without applying.
    internal static IReadOnlyList<DesktopDiagnostic> GetIgnoredOptions(DesktopWindowHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ColorScheme == Runic.Platform.DesktopColorScheme.NoPreference) return [];
        return
        [
            new DesktopDiagnostic(
                DesktopErrorCategory.CapabilityDenied,
                "color-scheme-unsupported",
                "The GTK 4 WebKitGTK 6.0 window ignores DesktopWindowOptions.ColorScheme and follows the system's light or dark appearance.",
                Retryable: false,
                Remediation: "WebKitGTK follows the GTK theme for every window. Remove the option on Linux, or style the page with its own light and dark themes.")
            {
                Severity = DesktopDiagnosticSeverity.Warning,
                Option = "DesktopWindowOptions." + nameof(DesktopWindowOptions.ColorScheme),
            },
        ];
    }

    private static DesktopDiagnostic Unsupported(string option, string message, string? remediation = null) => new(
        DesktopErrorCategory.CapabilityDenied,
        "window-option-unsupported",
        message,
        Retryable: false,
        Remediation: remediation ?? "Remove the option, or select the Gtk3WebKit41 backend.")
    {
        Option = "DesktopWindowOptions." + option,
    };

    private bool OnCloseRequest()
    {
        if (Volatile.Read(ref _forceClose) != 0)
        {
            return false;
        }

        if (_options?.CloseRequested is { } closeRequested)
        {
            closeRequested();
            return true;
        }

        QueueCleanupAfterDestroy();
        return false;
    }

    private bool OnUnhandledKeyPressed(Gtk.EventControllerKey controller, Gtk.EventControllerKey.KeyPressedSignalArgs args)
    {
        var command = GetZoomCommand(controller, args);
        if (_webView is not { } webView)
        {
            return false;
        }
        if (command == PageZoomCommand.None)
        {
            // The menu bar's shortcuts (B10), for every presentation.
            return TryRunShortcut(controller, args);
        }
        webView.SetZoomLevel(WebKitGtkKeyboard.Apply(command, webView.GetZoomLevel()));
        return true;
    }

    // The produced symbol first; when it is no zoom key, the unshifted and shifted
    // symbols of the same physical key in the event's layout.
    private static PageZoomCommand GetZoomCommand(Gtk.EventControllerKey controller, Gtk.EventControllerKey.KeyPressedSignalArgs args)
    {
        var state = (uint)args.State;
        var command = WebKitGtkKeyboard.GetZoomCommand(args.Keyval, state);
        if (command != PageZoomCommand.None || !WebKitGtkKeyboard.IsZoomModifier(state)
            || controller.GetCurrentEvent() is not Gdk.KeyEvent keyEvent
            || controller.GetWidget() is not { } widget)
        {
            return command;
        }
        Span<uint> levels = stackalloc uint[2];
        var found = KeyLevels.Find(widget.GetDisplay().Handle.DangerousGetHandle(), args.Keycode, keyEvent.GetLayout(), levels);
        return WebKitGtkKeyboard.GetZoomCommand(args.Keyval, state, levels[..found], args.Keycode);
    }

    // Connected for the zoom handler only, as the detail of script-message-received.
    private void OnZoomMessage(UserContentManager _, UserContentManager.ScriptMessageReceivedSignalArgs args)
    {
        var command = WebKitGtkKeyboard.GetWheelZoomCommand(args.Value.ToInt32());
        if (command != PageZoomCommand.None && _webView is { } webView)
        {
            webView.SetZoomLevel(WebKitGtkKeyboard.Apply(command, webView.GetZoomLevel()));
        }
    }

    // The script runs in every frame (D037.2); a report from a subframe of another origin than the presented one refuses
    // the drop. The page's effect chooses among the actions the drop offers (Gtk4FileDrops).
    private void OnFileDropMessage(UserContentManager _, UserContentManager.ScriptMessageReceivedSignalArgs args)
    {
        if (!IsOpen) return;
        var report = FileDropSupport.DecodeReport(args.Value.ToString());
        _drops?.PageReport(report.Label, refused: !GtkDragSupport.AcceptsReport(_presentedUrl, report.Origin), report.Effect);
    }

    private void OnMediaFramesMessage(UserContentManager _, UserContentManager.ScriptMessageReceivedSignalArgs args)
    {
        // Also before the window reports itself open: a frame the first page loads must not be missed.
        _mediaFrames?.Receive(args.Value.ToString());
    }

    // The drop's paths and the actions it offers (gdk_drop_get_actions).
    internal void NativeFileDrop(IReadOnlyList<string> paths, Runic.Platform.DesktopDragEffects offered)
    {
        if (IsOpen) _drops?.NativeDrop(paths, [], GtkDragSupport.OfferedDropEffect(offered), offered);
    }

    private void DetachZoomHandlers(Gtk.EventControllerKey? keys, UserContentManager? contentManager)
    {
        if (keys is not null)
        {
            keys.OnKeyPressed -= OnUnhandledKeyPressed;
        }
        if (contentManager is not null)
        {
            UserContentManager.ScriptMessageReceivedSignal.Disconnect(contentManager, OnZoomMessage);
            UserContentManager.ScriptMessageReceivedSignal.Disconnect(contentManager, OnFileDropMessage);
            contentManager.UnregisterScriptMessageHandler(FileDropSupport.WebKitMessageHandler, WebKitGtkKeyboard.ScriptWorld);
            if (_mediaFrames is not null)
            {
                UserContentManager.ScriptMessageReceivedSignal.Disconnect(contentManager, OnMediaFramesMessage);
                contentManager.UnregisterScriptMessageHandler(WebKitGtkMediaFrames.MessageHandler, WebKitGtkKeyboard.ScriptWorld);
            }
            contentManager.UnregisterScriptMessageHandler(WebKitGtkKeyboard.ZoomMessageHandler, WebKitGtkKeyboard.ScriptWorld);
        }
    }

    // An unhandled request is denied by WebKit, so a grant must allow explicitly.
    private void OnPermissionRequest(nint webView, nint request)
    {
        if (IsOwnOpenView(webView) && _webView is { } view && AllowsMediaCapture(view, request))
        {
            PermissionRequestSignal.Allow(request);
        }
        else
        {
            PermissionRequestSignal.Deny(request);
        }
    }

    // permission-request on the view, connected through GObject for the reason WebProcessTerminatedSignal gives. The
    // connection ends when the view is finalized.
    private static unsafe partial class PermissionRequestSignal
    {
        private static readonly nint WebKit = WebContentSignals.WebKitLibrary;
        private static readonly nint PermissionRequestAllow = NativeLibrary.GetExport(WebKit, "webkit_permission_request_allow");
        private static readonly nint PermissionRequestDeny = NativeLibrary.GetExport(WebKit, "webkit_permission_request_deny");
        private static readonly nint UserMediaPermissionRequestGetType = NativeLibrary.GetExport(WebKit, "webkit_user_media_permission_request_get_type");
        private static readonly nint DeviceInfoPermissionRequestGetType = NativeLibrary.GetExport(WebKit, "webkit_device_info_permission_request_get_type");
        private static readonly nint UserMediaPermissionIsForDisplayDevice = NativeLibrary.GetExport(WebKit, "webkit_user_media_permission_is_for_display_device");

        internal static void Connect(Gtk4WindowHost host, WebView webView)
        {
            var context = GCHandle.Alloc(host);
            var handler = ConnectData(webView.Handle.DangerousGetHandle(), "permission-request",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnRequest, GCHandle.ToIntPtr(context),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Release, 0);
            if (handler == 0)
            {
                context.Free();
                throw new InvalidOperationException("GTK rejected the 'permission-request' signal handler.");
            }
        }

        internal static void Allow(nint request) => ((delegate* unmanaged[Cdecl]<nint, void>)PermissionRequestAllow)(request);

        internal static void Deny(nint request) => ((delegate* unmanaged[Cdecl]<nint, void>)PermissionRequestDeny)(request);

        internal static bool IsUserMedia(nint request) => IsA(request, UserMediaPermissionRequestGetType);

        internal static bool IsDeviceInfo(nint request) => IsA(request, DeviceInfoPermissionRequestGetType);

        internal static bool IsForDisplayDevice(nint request) =>
            ((delegate* unmanaged[Cdecl]<nint, int>)UserMediaPermissionIsForDisplayDevice)(request) != 0;

        private static bool IsA(nint instance, nint getType) =>
            TypeCheckInstanceIsA(instance, ((delegate* unmanaged[Cdecl]<nuint>)getType)()) != 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int OnRequest(nint webView, nint request, nint context)
        {
            try
            {
                if (GCHandle.FromIntPtr(context).Target is Gtk4WindowHost host)
                {
                    host.OnPermissionRequest(webView, request);
                    return 1;
                }
            }
            catch
            {
                // Native callbacks cannot unwind through GTK.
            }
            // WebKit's default handler denies it.
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Release(nint context, nint closure) => GCHandle.FromIntPtr(context).Free();

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nuint ConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_type_check_instance_is_a")]
        private static partial int TypeCheckInstanceIsA(nint instance, nuint type);
    }

    // A crashed web process leaves the view blank (#313): report it and reload, a few times at most.
    private void OnWebProcessTerminated(nint webView, int reason)
    {
        if (!IsOpen || _webView is not { } view || view.Handle.DangerousGetHandle() != webView)
        {
            return;
        }
        _mediaFrames?.Reset();
        var (reload, diagnostic) = _webProcess.Terminated(reason, Environment.TickCount64);
        if (reload)
        {
            view.Reload();
        }
        _options?.DiagnosticSink?.Invoke(diagnostic);
    }

    // Connected through GObject, not a GirCore event: a GirCore signal handler that has run keeps the view from being
    // finalized, and closing waits for that. The connection ends when the view is finalized.
    private static unsafe partial class WebProcessTerminatedSignal
    {
        internal static void Connect(Gtk4WindowHost host, WebView webView)
        {
            var context = GCHandle.Alloc(host);
            var handler = ConnectData(webView.Handle.DangerousGetHandle(), "web-process-terminated",
                (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnTerminated, GCHandle.ToIntPtr(context),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Release, 0);
            if (handler == 0)
            {
                context.Free();
                throw new InvalidOperationException("GTK rejected the 'web-process-terminated' signal handler.");
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnTerminated(nint webView, int reason, nint context)
        {
            try
            {
                (GCHandle.FromIntPtr(context).Target as Gtk4WindowHost)?.OnWebProcessTerminated(webView, reason);
            }
            catch
            {
                // Native callbacks cannot unwind through GTK.
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Release(nint context, nint closure) => GCHandle.FromIntPtr(context).Free();

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nuint ConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);
    }

    // The window takes the page's <title>, as on GTK 3, WebView2 and in a browser: window lists and screen readers
    // name the window by it. "Runic Desktop" stays until the page has a title.
    private static unsafe partial class TitleSignal
    {
        internal static void Connect(Gtk4WindowHost host, WebView webView)
        {
            var context = GCHandle.Alloc(host);
            var handler = ConnectData(webView.Handle.DangerousGetHandle(), "notify::title",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnTitle, GCHandle.ToIntPtr(context),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Release, 0);
            if (handler == 0)
            {
                context.Free();
                throw new InvalidOperationException("GTK rejected the 'notify::title' signal handler.");
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnTitle(nint webView, nint property, nint context)
        {
            try
            {
                if (GCHandle.FromIntPtr(context).Target is Gtk4WindowHost { _window: { } window, _webView: { } view }
                    && view.GetTitle() is { Length: > 0 } title)
                {
                    window.Title = title;
                }
            }
            catch
            {
                // Native callbacks cannot unwind through GTK.
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Release(nint context, nint closure) => GCHandle.FromIntPtr(context).Free();

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nuint ConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);
    }

    // DesktopWebContentPolicy (SEC-04), before the first load: developer extras, top-level navigations, new windows and
    // downloads. See WebKitGtkWebContent for how the main frame's navigations are told apart.
    private void ApplyWebContent(WebView webView, WebContentRules rules)
    {
        _webContent = new WebKitGtkWebContent(rules, WebContentSignals.CanIdentifyMainFrame);
        webView.GetSettings().SetEnableDeveloperExtras(rules.DevToolsEnabled);
        WebContentSignals.Connect(this, webView);
        if (!rules.AllowsDownloads)
        {
            // <a download> and other downloads WebKit starts without a response decision.
            (_downloadSession, _downloadHandler) = WebContentSignals.ConnectDownloadStarted(this, webView);
        }
    }

    private void DisconnectDownloads()
    {
        if (_downloadSession == 0) return;
        WebContentSignals.DisconnectAndRelease(_downloadSession, _downloadHandler);
        _downloadSession = 0;
        _downloadHandler = 0;
    }

    private const int PolicyDecisionNavigationAction = 0;
    private const int PolicyDecisionNewWindowAction = 1;
    private const int PolicyDecisionResponse = 2;

    // Whether this handler decided; WebKit's default handling applies otherwise.
    private bool DecidePolicy(nint webView, nint decision, int type)
    {
        if (!IsOwnOpenView(webView) || _webContent is not { } content) return false;
        switch (type)
        {
            case PolicyDecisionNavigationAction:
            {
                var (url, userGesture) = WebContentSignals.GetNavigation(WebContentSignals.GetNavigationAction(decision));
                return Apply(decision, content.ForNavigationAction(url, userGesture), url);
            }
            case PolicyDecisionNewWindowAction:
            {
                // Ignored, so WebKit emits no create signal for it.
                var (url, userGesture) = WebContentSignals.GetNavigation(WebContentSignals.GetNavigationAction(decision));
                WebContentSignals.IgnorePolicy(decision);
                OpenNewWindow(content.Rules.ForNewWindow(url, userGesture), url);
                return true;
            }
            case PolicyDecisionResponse:
                if (WebContentSignals.IsMainFrameMainResource(decision)
                    && content.ForMainFrameResponse(WebContentSignals.GetResponseUri(decision)) is var navigation and not WebContentRules.Decision.Allow)
                {
                    return Apply(decision, navigation, WebContentSignals.GetResponseUri(decision));
                }
                if (content.CancelsResponse(WebContentSignals.IsMimeTypeSupported(decision)))
                {
                    WebContentSignals.IgnorePolicy(decision);
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private bool Apply(nint decision, WebContentRules.Decision navigation, string? url)
    {
        if (navigation == WebContentRules.Decision.Allow) return false;
        WebContentSignals.IgnorePolicy(decision);
        if (navigation == WebContentRules.Decision.OpenExternally) OpenExternally(url);
        return true;
    }

    // window.open, and a target="_blank" WebKit did not ask decide-policy about. Never creates a view.
    private void OnCreate(nint webView, nint navigationAction)
    {
        if (!IsOwnOpenView(webView) || _webContent is not { } content) return;
        var (url, userGesture) = WebContentSignals.GetNavigation(navigationAction);
        OpenNewWindow(content.Rules.ForNewWindow(url, userGesture), url);
    }

    private void OpenNewWindow(WebContentRules.Decision decision, string? url)
    {
        if (decision == WebContentRules.Decision.OpenExternally)
        {
            OpenExternally(url);
        }
        else if (decision == WebContentRules.Decision.NavigateInWindow && url is not null)
        {
            // From WebKit's signal: load once it has returned.
            Gtk4Dispatcher.Instance.Post(() =>
            {
                try
                {
                    if (IsOpen) _webView?.LoadUri(url);
                }
                catch
                {
                    // Never unwind through GTK.
                }
            });
        }
    }

    // A failed main-frame load: one waiting for its response may still open externally, and WebKit's error page must not
    // replace the application's document for a URL the window would not show.
    private bool OnLoadFailed(nint webView, string? url)
    {
        if (!IsOwnOpenView(webView) || _webContent is not { } content) return false;
        if (content.ForFailedMainFrameLoad(url, out var suppress) == WebContentRules.Decision.OpenExternally)
        {
            OpenExternally(url);
        }
        return suppress;
    }

    private void OnDownloadStarted(nint download)
    {
        if (IsOpen && _webView is { } view && WebContentSignals.GetDownloadWebView(download) == view.Handle.DangerousGetHandle())
        {
            WebContentSignals.CancelDownload(download);
        }
    }

    private bool IsOwnOpenView(nint webView) => IsOpen && _webView is { } view && view.Handle.DangerousGetHandle() == webView;

    // A URL WebContentRules.ExternalTarget accepts, with the system's default handler (GtkUriLauncher, which asks the
    // OpenURI portal inside Flatpak), after WebKit's signal returned. A failure is not reported: the page cannot act on it.
    private void OpenExternally(string? url)
    {
        if (WebContentRules.ExternalTarget(url) is not { } target) return;
        Gtk4Dispatcher.Instance.Post(() =>
        {
            try
            {
                if (ExternalOpenerOverride is { } opener)
                {
                    opener(target);
                }
                else if (_window is { } window)
                {
                    var launcher = Gtk.UriLauncher.New(target);
                    _ = launcher.LaunchAsync(window).ContinueWith(static (launch, state) =>
                    {
                        _ = launch.Exception;
                        ((Gtk.UriLauncher)state!).Dispose();
                    }, launcher, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
            catch
            {
                // Never unwind through GTK.
            }
        });
    }

    // decide-policy, create and load-failed on the view, and download-started on its network session. Connected through
    // GObject, not GirCore events, for the reason WebProcessTerminatedSignal gives; the view's connections end when it is
    // finalized, the session's when the window closes.
    private static unsafe partial class WebContentSignals
    {
        internal static readonly nint WebKitLibrary = LoadWebKit();
        private static readonly nint WebKit = WebKitLibrary;
        private static readonly nint NavigationPolicyDecisionGetNavigationAction = Export("webkit_navigation_policy_decision_get_navigation_action");
        private static readonly nint NavigationActionGetRequest = Export("webkit_navigation_action_get_request");
        private static readonly nint NavigationActionIsUserGesture = Export("webkit_navigation_action_is_user_gesture");
        private static readonly nint UriRequestGetUri = Export("webkit_uri_request_get_uri");
        private static readonly nint PolicyDecisionIgnore = Export("webkit_policy_decision_ignore");
        private static readonly nint ResponsePolicyDecisionGetRequest = Export("webkit_response_policy_decision_get_request");
        private static readonly nint ResponsePolicyDecisionIsMimeTypeSupported = Export("webkit_response_policy_decision_is_mime_type_supported");
        private static readonly nint ResponsePolicyDecisionIsMainFrameMainResource = Export("webkit_response_policy_decision_is_main_frame_main_resource");
        private static readonly nint WebViewGetNetworkSession = Export("webkit_web_view_get_network_session");
        private static readonly nint DownloadGetWebView = Export("webkit_download_get_web_view");
        private static readonly nint DownloadCancel = Export("webkit_download_cancel");

        // WebKitGTK 6.0 is 2.40 or later, which has the response API.
        internal static bool CanIdentifyMainFrame => true;

        internal static void Connect(Gtk4WindowHost host, WebView webView)
        {
            var view = webView.Handle.DangerousGetHandle();
            ConnectSignal(host, view, "decide-policy", (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, int>)&OnDecidePolicy);
            ConnectSignal(host, view, "create", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)&OnCreate);
            ConnectSignal(host, view, "load-failed", (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, nint, nint, int>)&OnLoadFailed);
        }

        // Connects download-started on the view's network session and keeps a reference to the session for the
        // disconnection.
        internal static (nint Session, ulong Handler) ConnectDownloadStarted(Gtk4WindowHost host, WebView webView)
        {
            var session = ((delegate* unmanaged[Cdecl]<nint, nint>)WebViewGetNetworkSession)(webView.Handle.DangerousGetHandle());
            if (session == 0) throw new InvalidOperationException("WebKitGTK did not provide the view's network session.");
            ObjectRef(session);
            try
            {
                return (session, ConnectSignal(host, session, "download-started", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnDownloadStarted));
            }
            catch
            {
                ObjectUnref(session);
                throw;
            }
        }

        internal static void DisconnectAndRelease(nint instance, ulong handler)
        {
            HandlerDisconnect(instance, handler);
            ObjectUnref(instance);
        }

        internal static nint GetNavigationAction(nint decision) =>
            ((delegate* unmanaged[Cdecl]<nint, nint>)NavigationPolicyDecisionGetNavigationAction)(decision);

        // The navigation's URL and whether a user gesture (a click or key press) started it.
        internal static (string? Url, bool UserGesture) GetNavigation(nint navigationAction)
        {
            if (navigationAction == 0) return (null, false);
            var request = ((delegate* unmanaged[Cdecl]<nint, nint>)NavigationActionGetRequest)(navigationAction);
            var userGesture = ((delegate* unmanaged[Cdecl]<nint, int>)NavigationActionIsUserGesture)(navigationAction) != 0;
            return (RequestUri(request), userGesture);
        }

        internal static void IgnorePolicy(nint decision) => ((delegate* unmanaged[Cdecl]<nint, void>)PolicyDecisionIgnore)(decision);

        internal static bool IsMainFrameMainResource(nint decision) =>
            ((delegate* unmanaged[Cdecl]<nint, int>)ResponsePolicyDecisionIsMainFrameMainResource)(decision) != 0;

        internal static bool IsMimeTypeSupported(nint decision) =>
            ((delegate* unmanaged[Cdecl]<nint, int>)ResponsePolicyDecisionIsMimeTypeSupported)(decision) != 0;

        internal static string? GetResponseUri(nint decision) =>
            RequestUri(((delegate* unmanaged[Cdecl]<nint, nint>)ResponsePolicyDecisionGetRequest)(decision));

        internal static nint GetDownloadWebView(nint download) => ((delegate* unmanaged[Cdecl]<nint, nint>)DownloadGetWebView)(download);

        internal static void CancelDownload(nint download) => ((delegate* unmanaged[Cdecl]<nint, void>)DownloadCancel)(download);

        private static string? RequestUri(nint request) => request == 0
            ? null
            : Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<nint, nint>)UriRequestGetUri)(request));

        private static ulong ConnectSignal(Gtk4WindowHost host, nint instance, string signal, nint callback)
        {
            var context = GCHandle.Alloc(host);
            var handler = ConnectData(instance, signal, callback, GCHandle.ToIntPtr(context),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Release, 0);
            if (handler == 0)
            {
                context.Free();
                throw new InvalidOperationException($"GTK rejected the '{signal}' signal handler.");
            }
            return handler;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int OnDecidePolicy(nint webView, nint decision, int type, nint context)
        {
            try
            {
                return (GCHandle.FromIntPtr(context).Target as Gtk4WindowHost)?.DecidePolicy(webView, decision, type) == true ? 1 : 0;
            }
            catch
            {
                // Native callbacks cannot unwind through GTK.
                return 0;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static nint OnCreate(nint webView, nint navigationAction, nint context)
        {
            try
            {
                (GCHandle.FromIntPtr(context).Target as Gtk4WindowHost)?.OnCreate(webView, navigationAction);
            }
            catch
            {
                // Native callbacks cannot unwind through GTK.
            }
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int OnLoadFailed(nint webView, int loadEvent, nint failingUri, nint error, nint context)
        {
            try
            {
                return (GCHandle.FromIntPtr(context).Target as Gtk4WindowHost)?.OnLoadFailed(webView, Marshal.PtrToStringUTF8(failingUri)) == true ? 1 : 0;
            }
            catch
            {
                // Native callbacks cannot unwind through GTK.
                return 0;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnDownloadStarted(nint session, nint download, nint context)
        {
            try
            {
                (GCHandle.FromIntPtr(context).Target as Gtk4WindowHost)?.OnDownloadStarted(download);
            }
            catch
            {
                // Native callbacks cannot unwind through GTK.
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Release(nint context, nint closure) => GCHandle.FromIntPtr(context).Free();

        private static nint LoadWebKit()
        {
            foreach (var name in Gtk4NativeLibraries.WebKit)
            {
                if (NativeLibrary.TryLoad(name, out var library)) return library;
            }
            throw new DllNotFoundException("WebKitGTK 6.0 could not be loaded.");
        }

        private static nint Export(string name) => NativeLibrary.GetExport(WebKit, name);

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
        private static partial ulong ConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_handler_disconnect")]
        private static partial void HandlerDisconnect(nint instance, ulong handler);

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_ref")]
        private static partial nint ObjectRef(nint instance);

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")]
        private static partial void ObjectUnref(nint instance);
    }

    // WebKitGTK does not identify the requesting frame: the top-level document must have the presented origin, and so
    // must every subframe (WebKitGtkMediaFrames). Screen capture is not part of the grant.
    private bool AllowsMediaCapture(WebView webView, nint request) =>
        (_options?.AllowedPermissions & DesktopPermissionGrant.MediaCapture) != 0 &&
        _presentedUrl is { } presented &&
        _mediaFrames is { HasForeignFrames: false } &&
        (PermissionRequestSignal.IsUserMedia(request)
            ? !PermissionRequestSignal.IsForDisplayDevice(request)
            : PermissionRequestSignal.IsDeviceInfo(request)) &&
        Uri.TryCreate(webView.GetUri(), UriKind.Absolute, out var current) &&
        Uri.Compare(presented, current, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static (int Width, int Height) FitToLargestMonitor(int width, int height)
    {
        if (Gdk.Display.GetDefault()?.GetMonitors() is not { } monitors)
        {
            return (width, height);
        }
        long largestArea = 0;
        (int Width, int Height)? largest = null;
        for (uint index = 0; index < monitors.GetNItems(); index++)
        {
            if (monitors.GetObject(index) is not Gdk.Monitor monitor)
            {
                continue;
            }
            monitor.GetGeometry(out var geometry);
            if ((long)geometry.Width * geometry.Height > largestArea)
            {
                largestArea = (long)geometry.Width * geometry.Height;
                largest = (geometry.Width, geometry.Height);
            }
        }
        return largest is { } area ? (Math.Max(1, Math.Min(width, area.Width)), Math.Max(1, Math.Min(height, area.Height))) : (width, height);
    }

    // GTK thread only. GTK 4 keeps the restored (unmaximized) size in default-width and default-height and updates it
    // when the user resizes the window, so it is the placement to save.
    private void UpdateState()
    {
        Volatile.Write(ref _stateUpdateQueued, 0);
        if (_window is not { } window || !IsOpen)
        {
            return;
        }
        window.GetDefaultSize(out var defaultWidth, out var defaultHeight);
        var maximized = window.IsMaximized();
        var fullScreen = window.IsFullscreen();
        var width = window.GetWidth();
        var height = window.GetHeight();
        var restoredWidth = Math.Max(1, defaultWidth > 0 ? defaultWidth : width);
        var restoredHeight = Math.Max(1, defaultHeight > 0 ? defaultHeight : height);
        // X11 only, and only once the window is mapped: an unmapped window has no position on screen yet.
        var position = window.IsVisible() ? Gtk4X11Placement.GetPosition(SurfaceOf(window)) : null;
        if (position is not null && !maximized && !fullScreen)
        {
            _normalPosition = position;
        }
        var state = new DesktopWindowState
        {
            X = position?.X,
            Y = position?.Y,
            Width = width > 0 ? width : restoredWidth,
            Height = height > 0 ? height : restoredHeight,
            IsMaximized = maximized,
            IsFullScreen = fullScreen,
            MonitorId = Gtk4Monitors.IdOf(window.GetSurface()),
            Placement = new DesktopWindowPlacement
            {
                X = _normalPosition?.X,
                Y = _normalPosition?.Y,
                Width = restoredWidth,
                Height = restoredHeight,
                Maximized = maximized,
                FullScreen = fullScreen && _options?.Kiosk != true,
                Origin = DesktopCoordinateOrigin.TopLeft,
            },
        };
        if (Equals(state, Volatile.Read(ref _state)))
        {
            return;
        }
        Volatile.Write(ref _state, state);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void QueueStateUpdate()
    {
        if (IsOpen && Interlocked.Exchange(ref _stateUpdateQueued, 1) == 0)
        {
            // From a later main-loop iteration, once GTK has applied the new size to the window's allocation.
            Gtk4Dispatcher.Instance.Post(() =>
            {
                try
                {
                    UpdateState();
                }
                catch
                {
                    // Never unwind through GTK.
                }
            });
        }
    }

    // Connected through GObject, not GirCore events, for the same reason as WebProcessTerminatedSignal: a GirCore
    // handler would keep the window from being finalized. The connections end when the window and surface are.
    private static unsafe partial class WindowStateSignals
    {
        internal static void Connect(Gtk4WindowHost host, Gtk.ApplicationWindow window)
        {
            var native = window.Handle.DangerousGetHandle();
            foreach (var property in (string[])["default-width", "default-height", "maximized", "fullscreened"])
            {
                ConnectNotify(host, native, property);
            }
            // The allocated size of a maximized or full-screen window follows its surface.
            if (window.GetSurface() is { } surface)
            {
                var nativeSurface = surface.Handle.DangerousGetHandle();
                ConnectNotify(host, nativeSurface, "width");
                ConnectNotify(host, nativeSurface, "height");
            }
        }

        private static void ConnectNotify(Gtk4WindowHost host, nint instance, string property)
        {
            var context = GCHandle.Alloc(host);
            var handler = ConnectData(instance, "notify::" + property,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnNotify, GCHandle.ToIntPtr(context),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Release, 0);
            if (handler == 0)
            {
                context.Free();
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnNotify(nint instance, nint property, nint context)
        {
            try
            {
                (GCHandle.FromIntPtr(context).Target as Gtk4WindowHost)?.QueueStateUpdate();
            }
            catch
            {
                // Native callbacks cannot unwind through GTK.
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Release(nint context, nint closure) => GCHandle.FromIntPtr(context).Free();

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nuint ConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);
    }

    private void OnDestroyed()
    {
        QueueCleanupAfterDestroy();
    }

    // close-request and destroy on the window, connected through GObject rather than as GirCore events: a GirCore
    // handler that has run keeps a reference to the window, so a window closed through its close request (the title
    // bar's button, gtk_window_close) was never finalized and closing never completed (#509). Disconnected when the
    // window's native objects are released.
    private static unsafe partial class WindowLifetimeSignals
    {
        internal static (nuint CloseRequest, nuint Destroy) Connect(Gtk4WindowHost host, Gtk.Window window)
        {
            var native = window.Handle.DangerousGetHandle();
            var closeRequest = ConnectSignal(host, native, "close-request", (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&OnCloseRequest);
            try
            {
                return (closeRequest, ConnectSignal(host, native, "destroy", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnDestroy));
            }
            catch
            {
                HandlerDisconnect(native, closeRequest);
                throw;
            }
        }

        internal static void Disconnect(Gtk.Window window, (nuint CloseRequest, nuint Destroy) handlers)
        {
            var native = window.Handle.DangerousGetHandle();
            if (handlers.CloseRequest != 0) HandlerDisconnect(native, handlers.CloseRequest);
            if (handlers.Destroy != 0) HandlerDisconnect(native, handlers.Destroy);
        }

        private static nuint ConnectSignal(Gtk4WindowHost host, nint instance, string signal, nint callback)
        {
            var context = GCHandle.Alloc(host);
            var handler = ConnectData(instance, signal, callback, GCHandle.ToIntPtr(context),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Release, 0);
            if (handler == 0)
            {
                context.Free();
                throw new InvalidOperationException($"GTK rejected the '{signal}' signal handler.");
            }
            return handler;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int OnCloseRequest(nint window, nint context)
        {
            try
            {
                return (GCHandle.FromIntPtr(context).Target as Gtk4WindowHost)?.OnCloseRequest() == true ? 1 : 0;
            }
            catch
            {
                // Native callbacks cannot unwind through GTK.
                return 0;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnDestroy(nint window, nint context)
        {
            try
            {
                (GCHandle.FromIntPtr(context).Target as Gtk4WindowHost)?.OnDestroyed();
            }
            catch
            {
                // Native callbacks cannot unwind through GTK.
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Release(nint context, nint closure) => GCHandle.FromIntPtr(context).Free();

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nuint ConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_handler_disconnect")]
        private static partial void HandlerDisconnect(nint instance, nuint handler);
    }

    private void DestroyWindow()
    {
        var window = GetWindow();
        var webView = GetWebView();
        window.Destroy();
        CleanupNative(window, webView);
    }

    private void QueueCleanupAfterDestroy()
    {
        if (Interlocked.Exchange(ref _cleanupQueued, 1) != 0)
        {
            return;
        }
        var window = _window;
        var webView = _webView;
        if (window is not null && webView is not null)
        {
            Gtk4Dispatcher.Instance.Post(() => CleanupNative(window, webView));
        }
    }

    private void CleanupNative(Gtk.ApplicationWindow window, WebView webView)
    {
        if (Interlocked.Exchange(ref _isOpen, 0) == 0)
        {
            return;
        }
        ReleaseDragOut();
        _drops?.Dispose();
        Gtk4FileDrops.Disconnect(this);
        DisconnectDownloads();
        Gtk4X11Placement.Disconnect(_moveWatch);
        _moveWatch = default;
        _webContent?.Clear();
        ReleaseMenus(webView);

        DetachZoomHandlers(_keys, _contentManager);
        _keys = null;
        _contentManager = null;
        WindowLifetimeSignals.Disconnect(window, _windowSignals);
        _windowSignals = default;
        webView.TryClose();
        webView.Dispose();
        window.Dispose();
        _window = null;
        _webView = null;
        var finalization = Interlocked.Exchange(ref _finalization, null);
        if (finalization is null)
        {
            CompleteClosed();
            return;
        }
        _ = CompleteClosedAfterFinalizationAsync(finalization);
    }

    private async Task CompleteClosedAfterFinalizationAsync(NativeObjectFinalizationProbe finalization)
    {
        try
        {
            await finalization.Completion.ConfigureAwait(false);
            CompleteClosed();
        }
        catch (Exception error)
        {
            _closed.TrySetException(error);
        }
    }

    private void CompleteClosed()
    {
        _closed.TrySetResult();
        Gtk4Application.Unregister(this);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReleaseDispatcherAsync()
    {
        if (Interlocked.Exchange(ref _dispatcherLease, 0) != 0)
        {
            await Gtk4Dispatcher.Instance.ReleaseAsync().ConfigureAwait(false);
        }
    }

    private Gtk.ApplicationWindow GetWindow() => _window ?? throw new InvalidOperationException("The GTK 4 window is not open.");

    private WebView GetWebView() => _webView ?? throw new InvalidOperationException("The GTK 4 WebKit view is not open.");
}

internal sealed partial class NativeObjectFinalizationProbe
{
    private const string GObjectLibrary = "libgobject-2.0.so.0";
    private static readonly GObjectWeakNotify WeakNotify = OnWeakNotify;
    private int _remaining;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Completion => _completion.Task;

    internal void Track(nint instance)
    {
        if (instance == 0)
        {
            throw new InvalidOperationException("GTK returned an invalid GObject handle.");
        }

        Interlocked.Increment(ref _remaining);
        var registration = GCHandle.Alloc(new WeakRegistration(this));
        NativeGObject.WeakRef(instance, WeakNotify, GCHandle.ToIntPtr(registration));
    }

    private void Finalized()
    {
        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _completion.TrySetResult();
        }
    }

    private static void OnWeakNotify(nint data, nint _)
    {
        var registration = GCHandle.FromIntPtr(data);
        try
        {
            ((WeakRegistration)registration.Target!).Probe.Finalized();
        }
        finally
        {
            registration.Free();
        }
    }

    private sealed class WeakRegistration(NativeObjectFinalizationProbe probe)
    {
        internal NativeObjectFinalizationProbe Probe { get; } = probe;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GObjectWeakNotify(nint data, nint whereTheObjectWas);

    private static partial class NativeGObject
    {
        [LibraryImport(GObjectLibrary, EntryPoint = "g_object_weak_ref")]
        internal static partial void WeakRef(nint @object, GObjectWeakNotify notify, nint data);
    }
}

[SupportedOSPlatform("linux")]
internal sealed class Gtk4Dispatcher
{
    private int _threadId;
    private SynchronizationContext? _context;
    private Gtk.Application? _application;
    private int _leases;

    internal static Gtk4Dispatcher Instance { get; } = new();

    internal bool CheckAccess => Environment.CurrentManagedThreadId == Volatile.Read(ref _threadId);

    internal Gtk.Application Application => _application ?? throw new InvalidOperationException("The GTK 4 dispatcher is not active.");

    internal void Attach(Gtk.Application application, SynchronizationContext context)
    {
        _application = application;
        _context = context;
        Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
    }

    internal void Detach(Gtk.Application application)
    {
        if (!ReferenceEquals(_application, application)) return;
        _application = null;
        _context = null;
        Volatile.Write(ref _threadId, 0);
        Volatile.Write(ref _leases, 0);
    }

    internal Task AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Gtk4Application.IsRunning || _context is null)
        {
            throw new InvalidOperationException("Start GTK 4 through DesktopEventLoop.Run(options.WithGtk4(), application) before opening a window.");
        }
        Interlocked.Increment(ref _leases);
        return Task.CompletedTask;
    }

    internal Task ReleaseAsync()
    {
        if (Volatile.Read(ref _leases) > 0) Interlocked.Decrement(ref _leases);
        return Task.CompletedTask;
    }

    internal Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (CheckAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var context = _context ?? throw new InvalidOperationException("The GTK 4 dispatcher has no synchronization context.");
        var work = new DispatchWork(action, cancellationToken);
        context.Post(static state => ((DispatchWork)state!).Run(), work);
        return work.WaitAsync();
    }

    internal void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var context = _context ?? throw new InvalidOperationException("The GTK 4 dispatcher is not active.");
        context.Post(static state => ((Action)state!).Invoke(), action);
    }

    private sealed class DispatchWork(Action action, CancellationToken cancellationToken)
    {
        private int _claimed;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task WaitAsync()
        {
            using var registration = cancellationToken.UnsafeRegister(static state => ((DispatchWork)state!).Cancel(), this);
            await _completion.Task.ConfigureAwait(false);
        }

        private void Cancel()
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) == 0)
            {
                _completion.TrySetCanceled(cancellationToken);
            }
        }

        internal void Run()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Cancel();
            }
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            {
                return;
            }
            try
            {
                action();
                _completion.TrySetResult();
            }
            catch (Exception error)
            {
                _completion.TrySetException(error);
            }
        }
    }
}

// gdk_display_map_keycode, which GirCore does not bind.
internal static unsafe partial class KeyLevels
{
    // Find's group for the symbols of every layout group.
    internal const uint AnyGroup = uint.MaxValue;

    // Writes the unshifted and shifted symbols of a physical key in one layout group.
    internal static int Find(nint display, uint keycode, uint group, Span<uint> levels)
    {
        GdkKeymapKey* entries = null;
        uint* keyvals = null;
        var count = 0;
        if (MapKeycode(display, keycode, &entries, &keyvals, &count) == 0)
        {
            return 0;
        }
        try
        {
            var found = 0;
            for (var index = 0; index < count && found < levels.Length; index++)
            {
                if ((group == AnyGroup || entries[index].Group == group) && entries[index].Level is 0 or 1)
                {
                    levels[found++] = keyvals[index];
                }
            }
            return found;
        }
        finally
        {
            Free(entries);
            Free(keyvals);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdkKeymapKey
    {
        internal uint Keycode;
        internal int Group;
        internal int Level;
    }

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_display_map_keycode")]
    private static partial int MapKeycode(nint display, uint keycode, GdkKeymapKey** keys, uint** keyvals, int* count);

    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_free")]
    private static partial void Free(void* memory);
}
