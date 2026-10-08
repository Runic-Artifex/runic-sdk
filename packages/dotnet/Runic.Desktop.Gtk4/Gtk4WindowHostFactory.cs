using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;

using Gtk;
using WebKit;

namespace Runic.Desktop.Gtk4;

/// <summary>Runs one GTK 4 and WebKitGTK 6 application on the Linux process main thread.</summary>
[SupportedOSPlatform("linux")]
public static partial class Gtk4Application
{
    private static readonly object Gate = new();
    private static readonly HashSet<Gtk4WindowHost> Hosts = [];
    private static int _state;

    /// <summary>Runs an application's asynchronous workload while GTK owns the calling main thread.</summary>
    /// <remarks>
    /// Call this directly from <c>Main</c>, before top-level awaits. The GTK 4
    /// provider does not support re-entering or restarting its native runtime.
    /// </remarks>
    public static int Run(Func<Task<int>> application) => RunCore(application, null);

    /// <summary>Runs GTK with the application's installed reverse-DNS identity.</summary>
    /// <remarks>Inside Flatpak this must match the package ID. The overload without an ID uses the Flatpak ID automatically.</remarks>
    public static int Run(Func<Task<int>> application, string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        return RunCore(application, applicationId);
    }

    private static int RunCore(Func<Task<int>> application, string? applicationId)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("GTK 4 and WebKitGTK 6 are available only on Linux.");
        }
        if (GetThreadId() != GetProcessId())
        {
            throw new InvalidOperationException("Call Gtk4Application.Run directly from the Linux process main thread, before awaiting application work.");
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
                throw new InvalidOperationException("Gtk4Application.Run supports one non-reentrant GTK 4 application lifetime per process.");
            }
            _state = 1;
        }

        LinuxDesktopRuntime.ClaimBackend(LinuxEmbeddedBackend.Gtk4WebKit6);
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

    internal static void Register(Gtk4WindowHost host)
    {
        lock (Gate)
        {
            if (_state != 1)
            {
                throw new InvalidOperationException("GTK 4 is stopping; open windows only inside Gtk4Application.Run.");
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

    [LibraryImport("libc", EntryPoint = "getpid")]
    private static partial int GetProcessId();
    [LibraryImport("libc", EntryPoint = "gettid")]
    private static partial int GetThreadId();
}

/// <summary>Creates explicitly selected GTK 4 and WebKitGTK 6 window hosts.</summary>
[SupportedOSPlatform("linux")]
public sealed class Gtk4WindowHostFactory : ILinuxDesktopWindowHostFactory
{
    /// <inheritdoc />
    public LinuxEmbeddedBackend Backend => LinuxEmbeddedBackend.Gtk4WebKit6;

    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsLinux() &&
        LinuxDesktopRuntime.CanUse(Backend) && Gtk4Runtime.IsAvailable;

    /// <inheritdoc />
    public DesktopWindowCapabilities Capabilities => Gtk4WindowHost.SupportedCapabilities;

    /// <inheritdoc />
    /// <remarks>GTK 4 windows reject X, Y, Centered, Transparent, HighContrast, ProfilePath, BrowserArguments and IconFile.</remarks>
    public IReadOnlyList<DesktopDiagnostic> ValidateOptions(DesktopWindowHostOptions options) =>
        Gtk4WindowHost.GetUnsupportedOptions(options);

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
            throw new InvalidOperationException("Start GTK 4 through Gtk4Application.Run before creating a Gtk4WindowHostFactory window host.");
        }

        return new Gtk4WindowHost();
    }
}

internal static class Gtk4Runtime
{
    private static readonly string[] GtkNames = ["libgtk-4.so.1"];
    private static readonly string[] WebKitNames = ["libwebkitgtk-6.0.so.4", "libwebkitgtk-6.0.so.0"];

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
internal sealed class Gtk4WindowHost : IDesktopNativeDispatchWindowHost
{
    internal const DesktopWindowCapabilities SupportedCapabilities = DesktopWindowCapabilities.NativeHandle |
        DesktopWindowCapabilities.Focus | DesktopWindowCapabilities.Minimize |
        DesktopWindowCapabilities.Maximize | DesktopWindowCapabilities.Resize |
        DesktopWindowCapabilities.CloseConfirmation;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Gtk.ApplicationWindow? _window;
    private WebView? _webView;
    private DesktopWindowHostOptions? _options;
    private Uri? _presentedUrl;
    private int _isOpen;
    private int _disposed;
    private int _forceClose;
    private int _dispatcherLease;
    private int _cleanupQueued;
    private int _createStarted;
    private NativeObjectFinalizationProbe? _finalization;

    public event EventHandler? Closed;

    public bool SupportsCloseConfirmation => true;

    public bool SupportsDocumentStartScript => true;

    public DesktopWindowCapabilities Capabilities => SupportedCapabilities;

    public bool SupportsNativeDispatch => true;

    public bool IsOpen => Volatile.Read(ref _isOpen) != 0;

    public nint NativeHandle => _window?.Handle.DangerousGetHandle() ?? 0;

    public bool CheckNativeAccess() => Gtk4Dispatcher.Instance.CheckAccess;

    public async ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Gtk4Application.IsRunning)
        {
            throw new InvalidOperationException("Open GTK 4 windows inside Gtk4Application.Run.");
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
            GetWindow().Present();
            _ = GetWebView().GrabFocus();
        }, cancellationToken));

    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) =>
        new(Gtk4Dispatcher.Instance.InvokeAsync(() => GetWindow().Minimize(), cancellationToken));

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

    public ValueTask ResizeAsync(uint width, uint height, CancellationToken cancellationToken = default) =>
        new(Gtk4Dispatcher.Instance.InvokeAsync(() => GetWindow().SetDefaultSize(checked((int)width), checked((int)height)), cancellationToken));

    public ValueTask MoveAsync(uint x, uint y, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("GTK 4 cannot position top-level windows on native Wayland."));

    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        new(Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            if (visible)
            {
                GetWindow().Present();
            }
            else
            {
                GetWindow().Hide();
            }
        }, cancellationToken));

    public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("GTK 4 frameless move is not yet implemented."));

    public ValueTask DispatchNativeAsync(Action action, CancellationToken cancellationToken = default)
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
            using var contextValue = new GObject.Value(webContext);
            webView = WebView.NewWithProperties(
                [new GObject.ConstructArgument("web-context", contextValue)]);
            window.Title = "Runic Desktop";
            window.SetDefaultSize(checked((int)options.Width), checked((int)options.Height));
            window.SetResizable(options.Resizable);
            window.SetDecorated(!options.Frameless && !options.Kiosk);
            if (options.MinimumWidth is not null || options.MinimumHeight is not null)
            {
                webView.SetSizeRequest(
                    options.MinimumWidth is { } minimumWidth ? checked((int)minimumWidth) : -1,
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
            window.SetChild(webView);
            // Hidden windows still need a native surface for portal ownership
            // and orderly Wayland teardown. Realizing does not map/show them.
            // Some GTK 4 Wayland session cleanup versions assume it exists.
            // Cast explicitly: Gtk.Native.Realize only initializes a surface
            // that already exists; Gtk.Widget.Realize creates it first.
            ((Gtk.Widget)window).Realize();
            finalization = new NativeObjectFinalizationProbe();
            finalization.Track(window.Handle.DangerousGetHandle());
            finalization.Track(webView.Handle.DangerousGetHandle());
            window.OnCloseRequest += OnCloseRequest;
            window.OnDestroy += OnDestroyed;
            webView.OnPermissionRequest += OnPermissionRequest;
            webView.LoadUri(url.AbsoluteUri);
            if (!options.Hidden)
            {
                window.Present();
            }
            if (options.Kiosk)
            {
                window.Fullscreen();
            }

            _window = window;
            _webView = webView;
            _finalization = finalization;
            Gtk4Application.Register(this);
            registered = true;
            Volatile.Write(ref _isOpen, 1);
        }
        catch
        {
            if (registered)
            {
                Gtk4Application.Unregister(this);
            }
            if (webView is not null)
            {
                webView.OnPermissionRequest -= OnPermissionRequest;
            }
            if (window is not null)
            {
                window.OnCloseRequest -= OnCloseRequest;
                window.OnDestroy -= OnDestroyed;
                window.Destroy();
            }
            webView?.Dispose();
            window?.Dispose();
            _window = null;
            _webView = null;
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
        if (options.X is not null)
            diagnostics.Add(Unsupported(nameof(DesktopWindowOptions.X), "GTK 4 does not support requested global window placement."));
        if (options.Y is not null)
            diagnostics.Add(Unsupported(nameof(DesktopWindowOptions.Y), "GTK 4 does not support requested global window placement."));
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
        if (!string.IsNullOrWhiteSpace(options.CustomArguments))
            diagnostics.Add(Unsupported(nameof(DesktopWindowOptions.BrowserArguments), "GTK 4 WebKit custom arguments are not supported."));
        if (!string.IsNullOrWhiteSpace(options.IconFile))
            diagnostics.Add(Unsupported(nameof(DesktopWindowOptions.IconFile), "GTK 4 window icon files are not implemented."));
        return diagnostics;
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

    private bool OnCloseRequest(Gtk.Window _, EventArgs __)
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

    // An unhandled request is denied by WebKit, so a grant must allow explicitly.
    private bool OnPermissionRequest(WebView webView, WebView.PermissionRequestSignalArgs request)
    {
        if (AllowsMediaCapture(webView, request.Request))
        {
            request.Request.Allow();
        }
        else
        {
            request.Request.Deny();
        }
        return true;
    }

    // WebKitGTK does not identify the requesting frame; the top-level document must
    // still have the presented origin. Screen capture is not part of the grant.
    private bool AllowsMediaCapture(WebView webView, PermissionRequest request) =>
        (_options?.AllowedPermissions & DesktopPermissionGrant.MediaCapture) != 0 &&
        _presentedUrl is { } presented &&
        request switch
        {
            UserMediaPermissionRequest media => !WebKit.Functions.UserMediaPermissionIsForDisplayDevice(media),
            DeviceInfoPermissionRequest => true,
            _ => false,
        } &&
        Uri.TryCreate(webView.GetUri(), UriKind.Absolute, out var current) &&
        Uri.Compare(presented, current, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0;

    private void OnDestroyed(Gtk.Widget _, EventArgs __)
    {
        QueueCleanupAfterDestroy();
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

        webView.OnPermissionRequest -= OnPermissionRequest;
        window.OnCloseRequest -= OnCloseRequest;
        window.OnDestroy -= OnDestroyed;
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
            throw new InvalidOperationException("Start GTK 4 through Gtk4Application.Run before opening a window.");
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
