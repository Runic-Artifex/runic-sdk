using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using Runic.Desktop;
using Runic.Desktop.Internal;

namespace Runic.Application.Views.Wpf;

/// <summary>Presents a Desktop surface inside the application's existing WPF visual tree.</summary>
/// <remarks>WPF owns placement, size, visibility, and the dispatcher. Insert the control before opening its surface.</remarks>
public sealed partial class RunicWebView : HwndHost
{
    private PresentationHost? _presentation;
    private nint _child;
    private bool _disposed;

    /// <summary>Creates an empty child presentation.</summary>
    public RunicWebView()
    {
        Focusable = true;
        Unloaded += (_, _) => _presentation?.CloseOnDispatcher();
        IsVisibleChanged += (_, _) => _presentation?.UpdateBounds();
    }

    /// <summary>Creates a Desktop host factory that presents one surface at a time in this control.</summary>
    /// <remarks>
    /// For <see cref="DesktopHostOptions.WindowHostFactory"/>, as <see cref="RunicViewHost"/> uses it for the Desktop
    /// host it starts itself. On a <see cref="RunicViewHost.Desktop"/> the application shares, the host presents its
    /// own surface through it instead.
    /// </remarks>
    public IDesktopWindowHostFactory CreateWindowHostFactory() => new HostFactory(this);

    /// <inheritdoc />
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _child = Native.CreateWindowEx(0, "STATIC", "", 0x50000000, 0, 0, 1, 1,
            hwndParent.Handle, 0, 0, 0);
        if (_child == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The WPF WebView child could not be created.");
        return new HandleRef(this, _child);
    }

    /// <inheritdoc />
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _presentation?.CloseOnDispatcher();
        Native.DestroyWindow(hwnd.Handle);
        _child = 0;
    }

    /// <inheritdoc />
    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        _presentation?.UpdateBounds();
    }

    /// <inheritdoc />
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _presentation?.FocusOnDispatcher();
    }

    /// <inheritdoc />
    protected override bool TabIntoCore(TraversalRequest request)
    {
        if (_presentation is not { IsOpen: true } presentation) return false;
        presentation.FocusOnDispatcher(request.FocusNavigationDirection is FocusNavigationDirection.Previous or FocusNavigationDirection.Last ? 2 : 1);
        return true;
    }

    // WebView2 asks to give focus back when Tab or Shift+Tab leaves the document's last or first tab stop: WPF
    // continues its own traversal from this control. When WPF has nowhere to go, WebView2 cycles inside the document.
    private bool TabOut(int reason) => MoveFocus(new TraversalRequest(
        reason == 2 ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));

    // The open presentation's controller, for native checks.
    internal WindowsWebView2Controller? Controller => _presentation?.Controller;

    // The announcer of the Desktop window presented here while its page has the keyboard focus, else null. Then the
    // focus is in WebView2's browser process, and NVDA ignores the WPF window's UI Automation notification, as for
    // Runic Desktop's own WebView2 window (I-184); WpfPlatformServices announces through the page's live region instead
    // (I-203). On the dispatcher.
    internal Func<string, DesktopAnnouncementPriority, CancellationToken, Task<bool>>? FocusedPageAnnouncer =>
        _presentation is { IsOpen: true, AnnounceInPage: { } announce } && WindowsWebView2Host.IsWebContentFocusedIn(_child) ? announce : null;

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    private sealed class HostFactory(RunicWebView view) : IDesktopWindowHostFactory
    {
        public bool IsSupported => WindowsWebView2Host.IsSupported;
        public DesktopWindowCapabilities Capabilities => PresentationHost.HostCapabilities;

        // The monitors of Windows, in the WebView2 window's units (physical pixels).
        public Runic.Platform.IDesktopMonitors Monitors => Runic.Desktop.Internal.WindowsMonitors.Shared;
        public IDesktopWindowHost Create() => new PresentationHost(view);
        public IReadOnlyList<DesktopDiagnostic> ValidateOptions(DesktopWindowHostOptions options) =>
            WpfPresentationOptions.Validate(options);
    }

    private sealed class PresentationHost(RunicWebView view) : IDesktopFileDropWindowHost, IDesktopMenuWindowHost, IDesktopPageAnnouncementHost
    {
        // WPF owns the window and its chrome, so the declared menu bar is the application's to show (a WPF Menu);
        // context menus are WPF ContextMenus over the control.
        internal const DesktopWindowCapabilities HostCapabilities =
            DesktopWindowCapabilities.Focus | DesktopWindowCapabilities.FileDrop | DesktopWindowCapabilities.ContextMenu;
        private WindowsWebView2Controller? _controller;
        private bool _closing;
        private bool _open;
        private Action? _closeRequested;
        public bool SupportsDocumentStartScript => true;
        public bool SupportsCloseConfirmation => true;
        // Files dropped on the child WebView reach the host, not the WPF shell's Drop event, which the HWND covers.
        public DesktopWindowCapabilities Capabilities => HostCapabilities;
        public IDesktopMenuCommandTarget? MenuCommands { get; set; }
        // Set by Runic Desktop's window before it opens this presentation.
        public Func<string, DesktopAnnouncementPriority, CancellationToken, Task<bool>>? AnnounceInPage { get; set; }
        public DesktopMenuBarPlacement MenuBarPlacement => DesktopMenuBarPlacement.None;
        public event EventHandler? MenuBarPlacementChanged { add { } remove { } }
        public ValueTask SetMenuBarAsync(IReadOnlyList<Runic.Platform.DesktopMenu> menus, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        // A page point is relative to this control, in CSS pixels times the page zoom.
        public ValueTask<Runic.Platform.DesktopMenuItem?> ShowContextMenuAsync(System.Collections.Immutable.ImmutableArray<Runic.Platform.DesktopMenuEntry> items,
            Runic.Platform.DesktopContextMenuOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(view.Dispatcher.InvokeAsync(() =>
            {
                if (!IsOpen) return Task.FromResult<Runic.Platform.DesktopMenuItem?>(null);
                return WpfContextMenu.Show(view, items, options, () => _controller?.ZoomFactor ?? 1, cancellationToken);
            }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task.Unwrap());
        }
        public event EventHandler? Closed;
        public event EventHandler<DesktopFileDropEventArgs>? FilesDropped;
        public bool IsOpen => Volatile.Read(ref _open);
        public nint NativeHandle => 0;
        internal WindowsWebView2Controller? Controller => _controller;

        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default) =>
            new(view.Dispatcher.InvokeAsync(() => OpenOnDispatcherAsync(url, options, cancellationToken)).Task.Unwrap());

        private async Task OpenOnDispatcherAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(view._disposed || _closing, this);
            if (view._child == 0 || !view.IsLoaded)
                throw new InvalidOperationException("Insert RunicWebView into a loaded WPF visual tree before opening the surface.");
            if (view._presentation is not null)
                throw new InvalidOperationException("This RunicWebView already presents a surface.");
            view._presentation = this;
            _closeRequested = options.CloseRequested;
            WindowsWebView2Controller? initializing = null;
            try
            {
                // WPF owns the STA and message pump. Reuse Desktop's controller and event/permission policy.
                initializing = await WindowsWebView2Controller.CreateAsync(view._child, new WebUiEmbeddedHostOptions
                {
                    ProfilePath = options.ProfilePath,
                    BrowserArguments = options.BrowserArguments,
                    Language = options.Language,
                });
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_closing || view._child == 0, this);
                if (options.StartupProgress is { } startupProgress)
                    initializing.RegisterStartupProgress(startupProgress);
                initializing.RegisterEvents(static _ => { }, RequestClose, options.AllowedPermissions, url);
                initializing.RegisterMoveFocusRequested(view.TabOut);
                initializing.ApplyWebContent(
                    new WebContentRules(options.WebContent, url, options.AdditionalOrigins),
                    target => WindowsWebView2Interop.OpenExternally(target),
                    // From a WebView2 event: navigate once the event has returned.
                    target => view.Dispatcher.BeginInvoke(() => _controller?.Navigate(target)));
                // The page's prefers-color-scheme (DesktopWindowHostOptions.ColorScheme); the WPF window keeps its own chrome.
                initializing.SetPreferredColorScheme(options.ColorScheme);
                if (options.DocumentStartScript is { } script)
                    await initializing.AddDocumentStartScriptAsync(script);
                await initializing.EnableFileDropsAsync(url, drop => FilesDropped?.Invoke(this, drop));
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_closing || view._child == 0, this);
                _controller = initializing;
                initializing = null;
                UpdateBounds();
                _controller.Navigate(url.AbsoluteUri);
                Volatile.Write(ref _open, true);
            }
            catch
            {
                initializing?.Dispose();
                CloseOnDispatcher();
                throw;
            }
        }

        private void RequestClose()
        {
            if (_closeRequested is { } request) request();
            else CloseOnDispatcher();
        }

        internal void UpdateBounds()
        {
            if (_controller is null || view._child == 0) return;
            // GetClientRect is in physical pixels, including per-monitor WPF DPI changes.
            Native.GetClientRect(view._child, out var bounds);
            _controller.Bounds = new Rectangle(0, 0, Math.Max(0, bounds.Right), Math.Max(0, bounds.Bottom));
            _controller.IsVisible = view.IsVisible;
        }

        internal void FocusOnDispatcher(int reason = 0) => _controller?.MoveFocus(reason);

        internal void CloseOnDispatcher()
        {
            if (_closing) return;
            _closing = true;
            Volatile.Write(ref _open, false);
            try { _controller?.Dispose(); }
            finally
            {
                _controller = null;
                if (ReferenceEquals(view._presentation, this)) view._presentation = null;
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }

        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => DispatchAsync(() =>
        {
            if (!IsOpen) throw new InvalidOperationException("The WPF presentation is closed.");
            _controller!.Navigate(url.AbsoluteUri);
        }, cancellationToken);
        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => DispatchAsync(CloseOnDispatcher, cancellationToken);
        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => DispatchAsync(() =>
        {
            if (!IsOpen) throw new InvalidOperationException("The WPF presentation is closed.");
            FocusOnDispatcher();
        }, cancellationToken);
        public ValueTask DisposeAsync() => CloseAsync();
        private ValueTask DispatchAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (view.Dispatcher.CheckAccess()) { action(); return ValueTask.CompletedTask; }
            return new(view.Dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Normal, token).Task);
        }
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => Unsupported();
        public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => Unsupported();
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => Unsupported();
        public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => Unsupported();
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => Unsupported();
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => Unsupported();
        private static ValueTask Unsupported() => ValueTask.FromException(new NotSupportedException("WPF owns the containing window and child layout."));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    private static partial class Native
    {
        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        internal static partial nint CreateWindowEx(uint extendedStyle, string className, string name, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyWindow(nint window);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetClientRect(nint window, out NativeRect bounds);
    }
}
