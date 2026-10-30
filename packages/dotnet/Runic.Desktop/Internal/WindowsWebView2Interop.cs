using System.Drawing;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Runic.Desktop.Internal;

// The stable Win32 ABI in the pinned SDK's build/native/include/WebView2.h.
// Only native pointers cross this boundary; no runtime-generated COM wrappers
// or assembly-location discovery are needed by NativeAOT applications.
internal static partial class WindowsWebView2Interop
{
    internal const int SettingsSlot = 3;
    internal const int NavigateSlot = 5;
    internal const int NavigationStartingSlot = 7;
    internal const int NavigationCompletedSlot = 15;
    internal const int ProcessFailedSlot = 25;
    internal const int NavigationCompletedIsSuccessSlot = 3;
    internal const int NavigationCompletedWebErrorStatusSlot = 4;
    internal const int ProcessFailedKindSlot = 3;
    internal const int NewWindowRequestedSlot = 44;
    internal const int DownloadStartingSlot = 75;
    internal const int SettingsDevToolsSlot = 12;
    internal const int SettingsGetDevToolsSlot = 11;
    internal const int NavigationStartingUriSlot = 3;
    internal const int NavigationStartingUserInitiatedSlot = 4;
    internal const int NavigationStartingCancelSlot = 8;
    internal const int NewWindowUriSlot = 3;
    internal const int NewWindowHandledSlot = 6;
    internal const int NewWindowUserInitiatedSlot = 8;
    internal const int DownloadStartingCancelSlot = 5;
    internal const int PermissionRequestedSlot = 23;
    internal const int AddScriptToExecuteOnDocumentCreatedSlot = 27;
    internal const int DocumentTitleChangedSlot = 46;
    internal const int DocumentTitleSlot = 48;
    internal const int WindowCloseRequestedSlot = 59;
    internal const int ControllerVisibleSlot = 4;
    internal const int ControllerBoundsSlot = 6;
    internal const int ControllerZoomFactorSlot = 7;
    internal const int ControllerFocusSlot = 12;
    internal const int ControllerMoveFocusRequestedSlot = 13;
    internal const int ControllerAcceleratorKeyPressedSlot = 19;
    internal const int ControllerCloseSlot = 24;
    internal const int ControllerWebViewSlot = 25;
    internal const int ControllerBackgroundSlot = 27;
    internal const int EnvironmentCreateControllerSlot = 3;
    internal const int EnvironmentBrowserVersionSlot = 5;
    internal const int PermissionUriSlot = 3;
    internal const int PermissionKindSlot = 4;
    internal const int PermissionStateSlot = 7;
    internal const int PermissionHandledSlot = 10;
    internal const int WebMessageReceivedSlot = 34;
    internal const int CallDevToolsProtocolMethodSlot = 36;
    // ICoreWebView2::GetDevToolsProtocolEventReceiver, ICoreWebView2DevToolsProtocolEventReceiver::add_DevToolsProtocolEventReceived
    // and ICoreWebView2DevToolsProtocolEventReceivedEventArgs::get_ParameterObjectAsJson.
    internal const int GetDevToolsProtocolEventReceiverSlot = 42;
    internal const int DevToolsProtocolEventReceivedSlot = 3;
    internal const int DevToolsProtocolEventParametersSlot = 3;
    internal const int MoveFocusReasonSlot = 3;
    internal const int MoveFocusHandledSlot = 5;
    internal const int WebMessageSourceSlot = 3;
    internal const int WebMessageAsStringSlot = 5;
    internal const int WebMessageAdditionalObjectsSlot = 6;
    internal const int ObjectCollectionCountSlot = 3;
    internal const int ObjectCollectionValueSlot = 4;
    internal const int FilePathSlot = 3;
    internal const int AcceleratorKeyKindSlot = 3;
    internal const int AcceleratorVirtualKeySlot = 4;
    internal const int AcceleratorBrowserKeyEnabledSlot = 10;
    internal static readonly Guid AcceleratorKeyPressedEventArgs2 = new("03b2c8c8-7799-4e34-bd66-ed26aa85f2bf");
    // ICoreWebView2_4::add_FrameCreated, ICoreWebView2FrameCreatedEventArgs::get_Frame, ICoreWebView2Frame::add_Destroyed,
    // ICoreWebView2Frame2::add_WebMessageReceived and ICoreWebView2Frame7::add_FrameCreated (nested frames).
    internal const int FrameCreatedSlot = 73;
    internal const int FrameCreatedFrameSlot = 3;
    internal const int FrameDestroyedSlot = 8;
    internal const int FrameWebMessageReceivedSlot = 22;
    internal const int FrameNestedFrameCreatedSlot = 30;
    internal static readonly Guid Frame2Interface = new("7a6a5834-d185-4dbf-b63f-4a9bc43107d4");
    internal static readonly Guid Frame7Interface = new("3598cfa2-d85d-5a9f-9228-4dde1f59ec64");
    internal static readonly Guid WebMessageReceivedEventArgs2 = new("06fc7ab7-c90c-4297-9389-33ca01cf6d5e");
    internal static readonly Guid WebView4Interface = new("20d02d59-6df2-42dc-bd06-f98a694b1302");
    // ICoreWebView2_13::get_Profile and ICoreWebView2Profile::put_PreferredColorScheme (COREWEBVIEW2_PREFERRED_COLOR_SCHEME
    // Auto 0, Light 1, Dark 2), WebView2 runtime 114.
    internal static readonly Guid WebView13Interface = new("f75f09a8-667e-4983-88d6-c8773f315e84");
    internal const int WebView13ProfileSlot = 105;
    internal const int ProfileGetPreferredColorSchemeSlot = 8;
    internal const int ProfilePreferredColorSchemeSlot = 9;

    // The COREWEBVIEW2_PREFERRED_COLOR_SCHEME for DesktopWindowOptions.ColorScheme.
    internal static int PreferredColorScheme(Runic.Platform.DesktopColorScheme colorScheme) => colorScheme switch
    {
        Runic.Platform.DesktopColorScheme.Light => 1,
        Runic.Platform.DesktopColorScheme.Dark => 2,
        _ => 0,
    };
    internal static readonly Guid FileInterface = new("f2c19559-6bc1-4583-a757-90021be9afec");
    private static readonly Lazy<nint> Loader = new(LoadLoader);

    static WindowsWebView2Interop()
    {
        NativeLibrary.SetDllImportResolver(typeof(WindowsWebView2Interop).Assembly,
            (name, _, _) => name == "WebView2Loader" ? Loader.Value : 0);
    }

    // NativeAOT binds these imports to the SDK's static loader. JIT builds use
    // the resolver above for the NuGet native asset's portable/RID layout.
    [LibraryImport("WebView2Loader")]
    private static unsafe partial int GetAvailableCoreWebView2BrowserVersionString(char* folder, nint* version);

    [LibraryImport("WebView2Loader")]
    private static unsafe partial int CreateCoreWebView2EnvironmentWithOptions(char* folder, char* profile, nint options, nint callback);

    private static nint LoadLoader()
    {
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", $"win-{architecture}", "native", "WebView2Loader.dll");
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "WebView2Loader.dll");
        return NativeLibrary.Load(path);
    }

    internal static unsafe nint Slot(nint instance, int index) => (*(nint**)instance)[index];
    internal static unsafe void AddRef(nint instance)
        => ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 1))(instance);
    internal static unsafe void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }

    internal static unsafe nint Query(nint instance, Guid iid)
    {
        nint result;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(
            instance, &iid, &result));
        return result;
    }

    internal static unsafe bool IsAvailable()
    {
        nint version = 0;
        try { return GetAvailableCoreWebView2BrowserVersionString(null, &version) >= 0 && version != 0; }
        finally { Marshal.FreeCoTaskMem(version); }
    }

    internal static unsafe void CreateEnvironment(string? profile, nint options, nint callback)
    {
        fixed (char* profilePath = profile) Marshal.ThrowExceptionForHR(CreateCoreWebView2EnvironmentWithOptions(null, profilePath, options, callback));
    }

    internal static unsafe void CreateController(nint environment, nint window, nint callback)
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(environment, EnvironmentCreateControllerSlot))(
            environment, window, callback));

    internal static unsafe nint GetPointer(nint instance, int slot)
    {
        nint result;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(instance, slot))(instance, &result));
        return result;
    }

    internal static unsafe int GetInteger(nint instance, int slot)
    {
        int result;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(instance, slot))(instance, &result));
        return result;
    }

    internal static unsafe nint GetIndexedPointer(nint instance, int slot, uint index)
    {
        nint result;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(instance, slot))(instance, index, &result));
        return result;
    }

    internal static unsafe double GetDouble(nint instance, int slot)
    {
        double result;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, double*, int>)Slot(instance, slot))(instance, &result));
        return result;
    }

    internal static unsafe uint GetUnsigned(nint instance, int slot)
    {
        uint result;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(instance, slot))(instance, &result));
        return result;
    }

    internal static unsafe void SetInteger(nint instance, int slot, int value)
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(instance, slot))(instance, value));

    internal static unsafe void Navigate(nint webView, string url)
    {
        fixed (char* value = url)
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(webView, NavigateSlot))(webView, value));
    }

    // Opens a URL that WebContentRules.ExternalTarget accepted with its default handler, as Runic.Platform's Windows
    // launcher does: the URL only, with no parameters string.
    // Tests observe it instead.
    internal static Action<string> OpenExternally { get; set; } = static url => _ = ShellExecute(0, "open", url, null, null, 1);

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ShellExecute(nint window, string verb, string file, string? parameters, string? directory, int show);

    internal static unsafe void AddScriptToExecuteOnDocumentCreated(nint webView, string script, nint callback)
    {
        fixed (char* value = script)
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, nint, int>)Slot(webView, AddScriptToExecuteOnDocumentCreatedSlot))(
                webView, value, callback));
    }

    internal static unsafe void CallDevToolsProtocolMethod(nint webView, string method, string parameters, nint callback)
    {
        fixed (char* name = method)
        fixed (char* json = parameters)
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, char*, nint, int>)Slot(webView, CallDevToolsProtocolMethodSlot))(
                webView, name, json, callback));
    }

    internal static unsafe nint GetDevToolsProtocolEventReceiver(nint webView, string eventName)
    {
        nint receiver;
        fixed (char* name = eventName)
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Slot(webView, GetDevToolsProtocolEventReceiverSlot))(
                webView, name, &receiver));
        return receiver;
    }

    internal static unsafe void SetBounds(nint controller, Rectangle bounds)
    {
        var rect = new NativeRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, NativeRect, int>)Slot(controller, ControllerBoundsSlot))(controller, rect));
    }

    internal static unsafe long AddEvent(nint webView, int slot, nint callback)
    {
        long token;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)Slot(webView, slot))(webView, callback, &token));
        return token;
    }

    internal static unsafe void RemoveEvent(nint webView, int slot, long token)
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(webView, slot))(webView, token));

    internal static unsafe void Close(nint controller)
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)Slot(controller, ControllerCloseSlot))(controller));

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect(int left, int top, int right, int bottom)
    {
        public readonly int Left = left;
        public readonly int Top = top;
        public readonly int Right = right;
        public readonly int Bottom = bottom;
    }
}

internal sealed class WindowsWebView2Controller : IDisposable
{
    private nint _controller;
    private nint _environment;
    private nint _webView;
    private nint _webView4;
    private nint _framesSource;
    private WebViewFrames? _frames;
    private readonly List<(nint Source, int Slot, long Token, IDisposable Handler)> _events = [];
    private readonly List<nint> _receivers = [];

    private WindowsWebView2Controller(nint environment, nint controller)
    {
        _webView = WindowsWebView2Interop.GetPointer(controller, WindowsWebView2Interop.ControllerWebViewSlot);
        _environment = environment;
        _controller = controller;
    }

    internal static async Task<WindowsWebView2Controller> CreateAsync(nint window, WebUiEmbeddedHostOptions options)
    {
        var environmentCompletion = new WebViewCompletion();
        using var environmentHandler = new WebViewComReference<IWebViewEnvironmentCompleted>(environmentCompletion);
        using var environmentOptions = new WebViewComReference<IWebViewEnvironmentOptions>(new WebViewEnvironmentOptions(options.BrowserArguments, options.Language));
        WindowsWebView2Interop.CreateEnvironment(options.ProfilePath, environmentOptions.Pointer, environmentHandler.Pointer);
        var environment = await environmentCompletion.Task;
        nint controller = 0;
        try
        {
            var controllerCompletion = new WebViewCompletion();
            using var controllerHandler = new WebViewComReference<IWebViewControllerCompleted>(controllerCompletion);
            WindowsWebView2Interop.CreateController(environment, window, controllerHandler.Pointer);
            controller = await controllerCompletion.Task;
            return new WindowsWebView2Controller(environment, controller);
        }
        catch
        {
            if (controller != 0)
            {
                try { WindowsWebView2Interop.Close(controller); }
                finally { WindowsWebView2Interop.Release(controller); }
            }
            WindowsWebView2Interop.Release(environment);
            throw;
        }
    }

    internal Rectangle Bounds { set => WindowsWebView2Interop.SetBounds(_controller, value); }
    internal bool IsVisible { set => WindowsWebView2Interop.SetInteger(_controller, WindowsWebView2Interop.ControllerVisibleSlot, value ? 1 : 0); }
    // The page zoom: CSS pixels times this are device-independent pixels.
    internal double ZoomFactor => WindowsWebView2Interop.GetDouble(_controller, WindowsWebView2Interop.ControllerZoomFactorSlot);
    internal void MoveFocus(int reason = 0) => WindowsWebView2Interop.SetInteger(_controller, WindowsWebView2Interop.ControllerFocusSlot, reason);
    internal void Navigate(string url) => WindowsWebView2Interop.Navigate(_webView, url);

    // Tab or Shift+Tab past the document's last or first tab stop raises MoveFocusRequested with reason Next (1) or
    // Previous (2). An embedding host that moves focus to its own controls returns true; WebView2 otherwise cycles
    // focus inside the document.
    internal void RegisterMoveFocusRequested(Func<int, bool> moveFocus) =>
        AddEvent<IWebViewMoveFocusRequested>(_controller, WindowsWebView2Interop.ControllerMoveFocusRequestedSlot, new WebViewEvent(args =>
        {
            var reason = WindowsWebView2Interop.GetInteger(args, WindowsWebView2Interop.MoveFocusReasonSlot);
            if (reason is 1 or 2 && moveFocus(reason))
                WindowsWebView2Interop.SetInteger(args, WindowsWebView2Interop.MoveFocusHandledSlot, 1);
        }));

    // A key down that is the shortcut of a declared menu item keeps the browser's own accelerator for it off, such as
    // Ctrl+P printing or F5 reloading; the page still receives the key first and reports it when unhandled
    // (EnableMenuKeysAsync). An older runtime without ICoreWebView2AcceleratorKeyPressedEventArgs2 keeps its accelerators.
    internal void RegisterAcceleratorKeys(Func<int, bool> isShortcut) =>
        AddEvent<IWebViewAcceleratorKeyPressed>(_controller, WindowsWebView2Interop.ControllerAcceleratorKeyPressedSlot, new WebViewEvent(args =>
        {
            // COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN (0) and SYSTEM_KEY_DOWN (2).
            var kind = WindowsWebView2Interop.GetInteger(args, WindowsWebView2Interop.AcceleratorKeyKindSlot);
            if (kind is not (0 or 2) || !isShortcut((int)WindowsWebView2Interop.GetUnsigned(args, WindowsWebView2Interop.AcceleratorVirtualKeySlot)))
                return;
            nint args2;
            try { args2 = WindowsWebView2Interop.Query(args, WindowsWebView2Interop.AcceleratorKeyPressedEventArgs2); }
            catch (InvalidCastException) { return; }
            try { WindowsWebView2Interop.SetInteger(args2, WindowsWebView2Interop.AcceleratorBrowserKeyEnabledSlot, 0); }
            finally { WindowsWebView2Interop.Release(args2); }
        }));

    // The keys the page left unhandled, reported by WindowsMenuKeys.Script from the top-level document of the
    // presented origin, with the script's token.
    internal async Task EnableMenuKeysAsync(Uri presentedUrl, Action<MenuKeyReport> pressed)
    {
        var token = Guid.NewGuid().ToString("N");
        AddEvent<IWebViewWebMessageReceived>(WindowsWebView2Interop.WebMessageReceivedSlot, new WebViewEvent(args =>
        {
            if (!PresentationSecurityPolicy.IsSameOrigin(presentedUrl, GetString(args, WindowsWebView2Interop.WebMessageSourceSlot))) return;
            string? message;
            try { message = GetString(args, WindowsWebView2Interop.WebMessageAsStringSlot); }
            catch (ArgumentException) { return; } // Not a string message: not ours.
            if (WindowsMenuKeys.Parse(message, token) is { } report) pressed(report);
        }));
        await AddDocumentStartScriptAsync(WindowsMenuKeys.Script(token));
    }

    // Native checks drive trusted input (for example a Tab key) through the DevTools protocol.
    internal async Task<string> CallDevToolsProtocolMethodAsync(string method, string parameters)
    {
        var completion = new WebViewDevToolsCompletion();
        using var handler = new WebViewComReference<IWebViewDevToolsCompleted>(completion);
        WindowsWebView2Interop.CallDevToolsProtocolMethod(_webView, method, parameters, handler.Pointer);
        return await completion.Task;
    }

    // WebView2 injects the script only once this asynchronous registration completes,
    // so the first navigation must wait for it.
    internal async Task AddDocumentStartScriptAsync(string script)
    {
        var completion = new WebViewScriptCompletion();
        using var handler = new WebViewComReference<IWebViewScriptAdded>(completion);
        WindowsWebView2Interop.AddScriptToExecuteOnDocumentCreated(_webView, script, handler.Pointer);
        await completion.Task;
    }
    // DesktopWindowOptions.ColorScheme for the page's prefers-color-scheme and WebView2's own controls. The preferred color
    // scheme belongs to the profile, so windows sharing a ProfilePath share it; NoPreference leaves the profile as it is.
    // A runtime without ICoreWebView2_13 keeps the system's scheme.
    internal void SetPreferredColorScheme(Runic.Platform.DesktopColorScheme colorScheme)
    {
        if (colorScheme == Runic.Platform.DesktopColorScheme.NoPreference) return;
        nint webView13;
        try { webView13 = WindowsWebView2Interop.Query(_webView, WindowsWebView2Interop.WebView13Interface); }
        catch (InvalidCastException) { return; }
        try
        {
            var profile = WindowsWebView2Interop.GetPointer(webView13, WindowsWebView2Interop.WebView13ProfileSlot);
            try
            {
                WindowsWebView2Interop.SetInteger(profile, WindowsWebView2Interop.ProfilePreferredColorSchemeSlot,
                    WindowsWebView2Interop.PreferredColorScheme(colorScheme));
            }
            finally { WindowsWebView2Interop.Release(profile); }
        }
        finally { WindowsWebView2Interop.Release(webView13); }
    }

    internal void SetTransparent()
    {
        var controller2 = WindowsWebView2Interop.Query(_controller, new Guid("c979903e-d4ca-4228-92eb-47ee3fa96eab"));
        // COREWEBVIEW2_COLOR is four bytes in A/R/G/B order; transparent is all zero.
        try { WindowsWebView2Interop.SetInteger(controller2, WindowsWebView2Interop.ControllerBackgroundSlot, 0); }
        finally { WindowsWebView2Interop.Release(controller2); }
    }

    // Dropped files reach the host as the File objects of the page's drop event, which carry their paths only on this
    // side: Runic's document-start script, which WebView2 runs in every frame, hands them over
    // (FileDropSupport.WebView2Script). The top-level document and the frames of the presented origin are heard, with the
    // script's token; a frame's messages arrive on the frame (ICoreWebView2Frame2), which FrameCreated hands over.
    internal async Task EnableFileDropsAsync(Uri presentedUrl, Action<DesktopFileDropEventArgs> dropped)
    {
        var token = Guid.NewGuid().ToString("N");
        void Receive(nint args)
        {
            if (ReadFileDrop(args, presentedUrl, token) is { } drop) dropped(drop);
        }
        AddEvent<IWebViewWebMessageReceived>(WindowsWebView2Interop.WebMessageReceivedSlot, new WebViewEvent(Receive));
        // ICoreWebView2_4; an older runtime without it delivers drops of the top-level document only.
        nint webView4;
        try { webView4 = WindowsWebView2Interop.Query(_webView, WindowsWebView2Interop.WebView4Interface); }
        catch (InvalidCastException) { webView4 = 0; }
        if (webView4 != 0)
        {
            _frames = new WebViewFrames(Receive);
            try { AddEvent<IWebViewFrameCreated>(webView4, WindowsWebView2Interop.FrameCreatedSlot, new WebViewEvent(_frames.Created)); }
            finally { _framesSource = webView4; }
        }
        await AddDocumentStartScriptAsync(FileDropSupport.WebView2Script(token));
    }

    internal static DesktopFileDropEventArgs? ReadFileDrop(nint args, Uri presentedUrl, string token)
    {
        if (!PresentationSecurityPolicy.IsSameOrigin(presentedUrl, GetString(args, WindowsWebView2Interop.WebMessageSourceSlot))) return null;
        string? message;
        try { message = GetString(args, WindowsWebView2Interop.WebMessageAsStringSlot); }
        catch (ArgumentException) { return null; } // Not a string message: not ours.
        if (FileDropMessage.Parse(message, token) is not { } envelope) return null;
        var args2 = WindowsWebView2Interop.Query(args, WindowsWebView2Interop.WebMessageReceivedEventArgs2);
        try
        {
            var objects = WindowsWebView2Interop.GetPointer(args2, WindowsWebView2Interop.WebMessageAdditionalObjectsSlot);
            if (objects == 0) return null;
            try
            {
                List<string> paths = [];
                var count = WindowsWebView2Interop.GetUnsigned(objects, WindowsWebView2Interop.ObjectCollectionCountSlot);
                for (uint index = 0; index < count; index++)
                {
                    var value = WindowsWebView2Interop.GetIndexedPointer(objects, WindowsWebView2Interop.ObjectCollectionValueSlot, index);
                    try
                    {
                        nint file;
                        try { file = WindowsWebView2Interop.Query(value, WindowsWebView2Interop.FileInterface); }
                        catch (InvalidCastException) { continue; }
                        try
                        {
                            if (GetString(file, WindowsWebView2Interop.FilePathSlot) is { Length: > 0 } path && Path.IsPathFullyQualified(path)
                                && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                                paths.Add(path);
                        }
                        finally { WindowsWebView2Interop.Release(file); }
                    }
                    finally { WindowsWebView2Interop.Release(value); }
                }
                return paths.Count == 0 ? null : new DesktopFileDropEventArgs(paths, envelope.Target) { Effect = envelope.Effect };
            }
            finally { WindowsWebView2Interop.Release(objects); }
        }
        finally { WindowsWebView2Interop.Release(args2); }
    }

    internal void RegisterEvents(
        Action<string> titleChanged,
        Action closeRequested,
        DesktopPermissionGrant permissions,
        Uri presentedUrl)
    {
        AddEvent<IWebViewTitleChanged>(WindowsWebView2Interop.DocumentTitleChangedSlot, new WebViewEvent(_ =>
        {
            var title = WindowsWebView2Interop.GetPointer(_webView, WindowsWebView2Interop.DocumentTitleSlot);
            try { titleChanged(Marshal.PtrToStringUni(title) ?? string.Empty); }
            finally { Marshal.FreeCoTaskMem(title); }
        }));
        AddEvent<IWebViewCloseRequested>(WindowsWebView2Interop.WindowCloseRequestedSlot, new WebViewEvent(_ => closeRequested()));
        AddEvent<IWebViewPermissionRequested>(WindowsWebView2Interop.PermissionRequestedSlot, new WebViewEvent(args =>
        {
            // Microphone (1) and camera (2), only for the presented origin; the URI names the requesting frame.
            var kind = WindowsWebView2Interop.GetInteger(args, WindowsWebView2Interop.PermissionKindSlot);
            var allowed = kind is 1 or 2 && (permissions & DesktopPermissionGrant.MediaCapture) != 0 &&
                PresentationSecurityPolicy.IsSameOrigin(presentedUrl, GetString(args, WindowsWebView2Interop.PermissionUriSlot));
            WindowsWebView2Interop.SetInteger(args, WindowsWebView2Interop.PermissionStateSlot, allowed ? 1 : 2);
            var args2 = WindowsWebView2Interop.Query(args, new Guid("74d7127f-9de6-4200-8734-42d6fb4ff741"));
            try { WindowsWebView2Interop.SetInteger(args2, WindowsWebView2Interop.PermissionHandledSlot, 1); }
            finally { WindowsWebView2Interop.Release(args2); }
        }));
    }

    // Whether the developer tools are enabled, for native checks.
    internal bool AreDevToolsEnabled
    {
        get
        {
            var settings = WindowsWebView2Interop.GetPointer(_webView, WindowsWebView2Interop.SettingsSlot);
            try { return WindowsWebView2Interop.GetInteger(settings, WindowsWebView2Interop.SettingsGetDevToolsSlot) != 0; }
            finally { WindowsWebView2Interop.Release(settings); }
        }
    }

    // DesktopWebContentPolicy (SEC-04): developer tools, top-level navigations, new-window requests and downloads.
    // openExternally receives a URL that WebContentRules.ExternalTarget accepted; navigate loads one in this window.
    internal void ApplyWebContent(WebContentRules rules, Action<string> openExternally, Action<string> navigate)
    {
        var settings = WindowsWebView2Interop.GetPointer(_webView, WindowsWebView2Interop.SettingsSlot);
        try { WindowsWebView2Interop.SetInteger(settings, WindowsWebView2Interop.SettingsDevToolsSlot, rules.DevToolsEnabled ? 1 : 0); }
        finally { WindowsWebView2Interop.Release(settings); }

        AddEvent<IWebViewNavigationStarting>(WindowsWebView2Interop.NavigationStartingSlot, new WebViewEvent(args =>
        {
            var url = GetString(args, WindowsWebView2Interop.NavigationStartingUriSlot);
            var userInitiated = WindowsWebView2Interop.GetInteger(args, WindowsWebView2Interop.NavigationStartingUserInitiatedSlot) != 0;
            var decision = rules.ForNavigation(url, userInitiated);
            if (decision == WebContentRules.Decision.Allow) return;
            WindowsWebView2Interop.SetInteger(args, WindowsWebView2Interop.NavigationStartingCancelSlot, 1);
            if (decision == WebContentRules.Decision.OpenExternally && WebContentRules.ExternalTarget(url) is { } target) openExternally(target);
        }));
        AddEvent<IWebViewNewWindowRequested>(WindowsWebView2Interop.NewWindowRequestedSlot, new WebViewEvent(args =>
        {
            var url = GetString(args, WindowsWebView2Interop.NewWindowUriSlot);
            var userInitiated = WindowsWebView2Interop.GetInteger(args, WindowsWebView2Interop.NewWindowUserInitiatedSlot) != 0;
            // Handled without a new window: no popup opens, and window.open returns null.
            WindowsWebView2Interop.SetInteger(args, WindowsWebView2Interop.NewWindowHandledSlot, 1);
            switch (rules.ForNewWindow(url, userInitiated))
            {
                case WebContentRules.Decision.OpenExternally when WebContentRules.ExternalTarget(url) is { } target:
                    openExternally(target);
                    break;
                case WebContentRules.Decision.NavigateInWindow when url is not null:
                    navigate(url);
                    break;
            }
        }));
        if (!rules.AllowsDownloads)
        {
            // ICoreWebView2_4; an older runtime without it cannot report downloads to the host.
            // Kept until Dispose, which removes the registration through it.
            try { _webView4 = WindowsWebView2Interop.Query(_webView, WindowsWebView2Interop.WebView4Interface); }
            catch (InvalidCastException) { return; }
            AddEvent<IWebViewDownloadStarting>(_webView4, WindowsWebView2Interop.DownloadStartingSlot, new WebViewEvent(args =>
                WindowsWebView2Interop.SetInteger(args, WindowsWebView2Interop.DownloadStartingCancelSlot, 1)));
        }
    }

    // What the WebView did before its page connected, for the connection timeout's description (I-176): the controller's
    // creation with the runtime's version, the first navigation's start and end, and a process that failed. A page that never reaches the server is otherwise
    // reported with "nothing recorded". The same steps go to signal, whose deadline the first navigation holds while it
    // is in progress.
    internal void RegisterStartupProgress(Action<string> record, Action<WebViewStartupSignal>? signal)
    {
        void Step(string description, WebViewStartupSignal kind)
        {
            record(description);
            signal?.Invoke(kind);
        }

        // The runtime's version, to tell a runtime update from a change of the application.
        string? version;
        try { version = GetString(_environment, WindowsWebView2Interop.EnvironmentBrowserVersionSlot); }
        catch (Exception exception) when (exception is COMException or ArgumentException) { version = null; }
        Step(string.IsNullOrEmpty(version) ? "WebView2 controller created" : $"WebView2 controller created (runtime {version})",
            WebViewStartupSignal.Step);
        var started = false;
        var completed = false;
        var endTrace = TraceFirstDocumentRequest(record, () => signal?.Invoke(WebViewStartupSignal.Step));
        AddEvent<IWebViewNavigationStarting>(WindowsWebView2Interop.NavigationStartingSlot, new WebViewEvent(_ =>
        {
            if (started) return;
            started = true;
            Step("WebView2 navigation started", WebViewStartupSignal.NavigationStarted);
        }));
        AddEvent<IWebViewNavigationCompleted>(WindowsWebView2Interop.NavigationCompletedSlot, new WebViewEvent(args =>
        {
            if (completed) return;
            completed = true;
            endTrace();
            if (WindowsWebView2Interop.GetInteger(args, WindowsWebView2Interop.NavigationCompletedIsSuccessSlot) != 0)
                Step("WebView2 navigation completed", WebViewStartupSignal.NavigationCompleted);
            else
                Step("WebView2 navigation failed: " + WebErrorStatus(
                    WindowsWebView2Interop.GetInteger(args, WindowsWebView2Interop.NavigationCompletedWebErrorStatusSlot)),
                    WebViewStartupSignal.Failed);
        }));
        AddEvent<IWebViewProcessFailed>(WindowsWebView2Interop.ProcessFailedSlot, new WebViewEvent(args =>
        {
            var kind = WindowsWebView2Interop.GetInteger(args, WindowsWebView2Interop.ProcessFailedKindSlot);
            record("WebView2 " + ProcessFailure(kind));
            // The browser process, or the page's render process: the page does not load. A helper process that exits,
            // such as the GPU process, is restarted.
            if (kind is 0 or 1 or 2) signal?.Invoke(WebViewStartupSignal.Failed);
        }));
    }

    // The first document request as WebView2's network stack reports it through the DevTools protocol (I-176): when
    // the request started and how it ended. A stall between "navigation started" and "document request started" is
    // spent before WebView2 issues the request; one after it, inside WebView2's network stack. The Network domain is
    // on from before the navigation until that request or the first navigation ends; the returned action ends it.
    private Action TraceFirstDocumentRequest(Action<string> record, Action requestStarted)
    {
        string? requestId = null;
        var ended = false;
        void End()
        {
            if (ended) return;
            ended = true;
            // The call's failure is the async method's task; only observed.
            if (_webView != 0) _ = CallDevToolsProtocolMethodAsync("Network.disable", "{}").ContinueWith(
                static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        bool IsFirstDocumentRequest(JsonElement parameters) =>
            requestId is not null && parameters.TryGetProperty("requestId", out var id) && id.ValueEquals(requestId);
        try
        {
            AddDevToolsEvent("Network.requestWillBeSent", parameters =>
            {
                if (ended || requestId is not null || !parameters.TryGetProperty("type", out var type) || !type.ValueEquals("Document")) return;
                requestId = parameters.GetProperty("requestId").GetString();
                record("WebView2 document request started");
                requestStarted();
            });
            AddDevToolsEvent("Network.responseReceived", parameters =>
            {
                if (ended || !IsFirstDocumentRequest(parameters)) return;
                var status = parameters.GetProperty("response").GetProperty("status").GetDouble();
                record(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"WebView2 document response: HTTP {status:0}"));
                End();
            });
            AddDevToolsEvent("Network.loadingFailed", parameters =>
            {
                if (ended || !IsFirstDocumentRequest(parameters)) return;
                var error = parameters.TryGetProperty("errorText", out var text) ? text.GetString() : null;
                var canceled = parameters.TryGetProperty("canceled", out var flag) && flag.ValueKind == JsonValueKind.True;
                record($"WebView2 document request failed: {(string.IsNullOrEmpty(error) ? "no error text" : error)}{(canceled ? " (canceled)" : string.Empty)}");
                End();
            });
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException)
        {
            // A runtime without DevTools protocol events keeps the other entries.
            record("WebView2 network events unavailable");
            return () => { };
        }
        _ = EnableNetworkTraceAsync();
        return End;

        async Task EnableNetworkTraceAsync()
        {
            try
            {
                await CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                if (!ended) record("WebView2 network events on");
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            {
                record("WebView2 network events unavailable");
            }
        }
    }

    // Events of the DevTools protocol, with their parameters; the receiver is kept until Dispose removes the handler.
    private void AddDevToolsEvent(string eventName, Action<JsonElement> received)
    {
        var receiver = WindowsWebView2Interop.GetDevToolsProtocolEventReceiver(_webView, eventName);
        _receivers.Add(receiver);
        AddEvent<IWebViewDevToolsEventReceived>(receiver, WindowsWebView2Interop.DevToolsProtocolEventReceivedSlot, new WebViewEvent(args =>
        {
            if (GetString(args, WindowsWebView2Interop.DevToolsProtocolEventParametersSlot) is not { Length: > 0 } json) return;
            try
            {
                using var parameters = JsonDocument.Parse(json);
                received(parameters.RootElement);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                // Not the documented shape: no entry.
            }
        }));
    }

    // COREWEBVIEW2_WEB_ERROR_STATUS.
    private static string WebErrorStatus(int status) => status switch
    {
        6 => "server unreachable",
        7 => "timeout",
        8 => "invalid HTTP response",
        9 => "connection aborted",
        10 => "connection reset",
        11 => "network disconnected",
        12 => "cannot connect",
        13 => "host name not resolved",
        14 => "operation canceled",
        16 => "unexpected error",
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"web error status {status}"),
    };

    // COREWEBVIEW2_PROCESS_FAILED_KIND.
    private static string ProcessFailure(int kind) => kind switch
    {
        0 => "browser process exited",
        1 => "render process exited",
        2 => "render process unresponsive",
        3 => "frame render process exited",
        4 => "utility process exited",
        5 => "sandbox helper process exited",
        6 => "GPU process exited",
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"process failed (kind {kind})"),
    };

    private static string? GetString(nint instance, int slot)
    {
        var value = WindowsWebView2Interop.GetPointer(instance, slot);
        try { return Marshal.PtrToStringUni(value); }
        finally { Marshal.FreeCoTaskMem(value); }
    }

    private void AddEvent<T>(int slot, T handler) where T : class => AddEvent(_webView, slot, handler);

    private void AddEvent<T>(nint source, int slot, T handler) where T : class
    {
        var reference = new WebViewComReference<T>(handler);
        try { _events.Add((source, slot, WindowsWebView2Interop.AddEvent(source, slot, reference.Pointer), reference)); }
        catch { reference.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_controller == 0) return;
        // Dispose on the owning STA, before its message loop stops.
        try
        {
            foreach (var entry in _events) WindowsWebView2Interop.RemoveEvent(entry.Source, entry.Slot + 1, entry.Token);
        }
        finally
        {
            try { WindowsWebView2Interop.Close(_controller); }
            finally
            {
                foreach (var entry in _events) entry.Handler.Dispose();
                _events.Clear();
                foreach (var receiver in _receivers) WindowsWebView2Interop.Release(receiver);
                _receivers.Clear();
                _frames?.Dispose();
                _frames = null;
                WindowsWebView2Interop.Release(_framesSource);
                _framesSource = 0;
                WindowsWebView2Interop.Release(_webView4);
                WindowsWebView2Interop.Release(_webView);
                WindowsWebView2Interop.Release(_controller);
                WindowsWebView2Interop.Release(_environment);
                _webView = _webView4 = _controller = _environment = 0;
            }
        }
    }
}

internal sealed class WebViewComReference<T> : IDisposable where T : class
{
    internal unsafe WebViewComReference(T value) => Pointer = (nint)ComInterfaceMarshaller<T>.ConvertToUnmanaged(value);
    internal nint Pointer { get; private set; }
    public unsafe void Dispose()
    {
        ComInterfaceMarshaller<T>.Free((void*)Pointer);
        Pointer = 0;
    }
}

[GeneratedComInterface, Guid("4e8a3389-c9d8-4bd2-b6b5-124fee6cc14d")]
internal partial interface IWebViewEnvironmentCompleted { [PreserveSig] int Invoke(int errorCode, nint result); }
[GeneratedComInterface, Guid("6c4819f3-c9b7-4260-8127-c9f5bde7f68c")]
internal partial interface IWebViewControllerCompleted { [PreserveSig] int Invoke(int errorCode, nint result); }
[GeneratedComInterface, Guid("f5f2b923-953e-4042-9f95-f3a118e1afd4")]
internal partial interface IWebViewTitleChanged { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("5c19e9e0-092f-486b-affa-ca8231913039")]
internal partial interface IWebViewCloseRequested { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("57213f19-00e6-49fa-8e07-898ea01ecbd2")]
internal partial interface IWebViewWebMessageReceived { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("15e1c6a3-c72a-4df3-91d7-d097fbec6bfd")]
internal partial interface IWebViewPermissionRequested { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("69035451-6dc7-4cb8-9bce-b2bd70ad289f")]
internal partial interface IWebViewMoveFocusRequested { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("9adbe429-f36d-432b-9ddc-f8881fbd76e3")]
internal partial interface IWebViewNavigationStarting { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("d4c185fe-c81c-4989-97af-2d3fa7ab5651")]
internal partial interface IWebViewNewWindowRequested { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("efedc989-c396-41ca-83f7-07f845a55724")]
internal partial interface IWebViewDownloadStarting { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("b29c7e28-fa79-41a8-8e44-65811c76dcb2")]
internal partial interface IWebViewAcceleratorKeyPressed { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("38059770-9baa-11eb-a8b3-0242ac130003")]
internal partial interface IWebViewFrameCreated { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("e371e005-6d1d-4517-934b-a8f1629c62a5")]
internal partial interface IWebViewFrameWebMessageReceived { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("59dd7b4c-9baa-11eb-a8b3-0242ac130003")]
internal partial interface IWebViewFrameDestroyed { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("d33a35bf-1c49-4f98-93ab-006e0533fe1c")]
internal partial interface IWebViewNavigationCompleted { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("79e0aea4-990b-42d9-aa1d-0fcc2e5bc7f1")]
internal partial interface IWebViewProcessFailed { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("e2fda4be-5456-406c-a261-3d452138362c")]
internal partial interface IWebViewDevToolsEventReceived { [PreserveSig] int Invoke(nint sender, nint args); }
[GeneratedComInterface, Guid("5c4889f0-5ef6-4c5a-952c-d8f1b92d0574")]
internal partial interface IWebViewDevToolsCompleted { [PreserveSig] int Invoke(int errorCode, nint result); }

[GeneratedComClass]
internal sealed partial class WebViewDevToolsCompletion : IWebViewDevToolsCompleted
{
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<string> Task => _completion.Task;
    // The result is a borrowed JSON string.
    public int Invoke(int errorCode, nint result)
    {
        if (errorCode < 0)
            _completion.TrySetException(new InvalidOperationException("The DevTools protocol call failed.", Marshal.GetExceptionForHR(errorCode)));
        else
            _completion.TrySetResult(Marshal.PtrToStringUni(result) ?? string.Empty);
        return 0;
    }
}

[GeneratedComClass]
internal sealed partial class WebViewCompletion : IWebViewEnvironmentCompleted, IWebViewControllerCompleted
{
    private readonly TaskCompletionSource<nint> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<nint> Task => _completion.Task;
    public int Invoke(int errorCode, nint result)
    {
        if (errorCode < 0 || result == 0)
            _completion.TrySetException(new InvalidOperationException("WebView2 creation failed.", Marshal.GetExceptionForHR(errorCode)));
        else
        {
            WindowsWebView2Interop.AddRef(result);
            if (!_completion.TrySetResult(result)) WindowsWebView2Interop.Release(result);
        }
        return 0;
    }
}

[GeneratedComInterface, Guid("b99369f3-9b11-47b5-bc6f-8e7895fcea17")]
internal partial interface IWebViewScriptAdded { [PreserveSig] int Invoke(int errorCode, nint id); }

[GeneratedComClass]
internal sealed partial class WebViewScriptCompletion : IWebViewScriptAdded
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Task => _completion.Task;
    // The identifier is a borrowed string that this host never removes by ID.
    public int Invoke(int errorCode, nint id)
    {
        if (errorCode < 0)
            _completion.TrySetException(new InvalidOperationException("WebView2 rejected the document-start script.", Marshal.GetExceptionForHR(errorCode)));
        else
            _completion.TrySetResult();
        return 0;
    }
}

[GeneratedComClass]
internal sealed partial class WebViewEvent(Action<nint> callback) : IWebViewTitleChanged, IWebViewCloseRequested, IWebViewPermissionRequested,
    IWebViewWebMessageReceived, IWebViewMoveFocusRequested, IWebViewNavigationStarting, IWebViewNewWindowRequested, IWebViewDownloadStarting,
    IWebViewAcceleratorKeyPressed,
    IWebViewFrameCreated, IWebViewFrameWebMessageReceived, IWebViewNavigationCompleted, IWebViewProcessFailed, IWebViewDevToolsEventReceived
{
    public int Invoke(nint sender, nint args)
    {
        try { callback(args); return 0; }
        catch (Exception exception) { return Marshal.GetHRForException(exception); }
    }
}

[GeneratedComInterface, Guid("2fde08a8-1e9a-4766-8c05-95a9ceb9d1c5")]
internal partial interface IWebViewEnvironmentOptions
{
    [PreserveSig] int GetAdditionalBrowserArguments(out nint value);
    [PreserveSig] int SetAdditionalBrowserArguments(nint value);
    [PreserveSig] int GetLanguage(out nint value);
    [PreserveSig] int SetLanguage(nint value);
    [PreserveSig] int GetTargetCompatibleBrowserVersion(out nint value);
    [PreserveSig] int SetTargetCompatibleBrowserVersion(nint value);
    [PreserveSig] int GetAllowSingleSignOnUsingOSPrimaryAccount(out int value);
    [PreserveSig] int SetAllowSingleSignOnUsingOSPrimaryAccount(int value);
}

[GeneratedComClass]
// The language is ICoreWebView2EnvironmentOptions::Language: the browser's UI language and its Accept-Language.
internal sealed partial class WebViewEnvironmentOptions(string? arguments, string? language = null) : IWebViewEnvironmentOptions
{
    private string? _arguments = arguments;
    private string? _language = language;
    private string? _version = "154.0.4258.31"; // CORE_WEBVIEW_TARGET_PRODUCT_VERSION in the pinned SDK.
    private int _allowSingleSignOn;
    public int GetAdditionalBrowserArguments(out nint value) { value = Marshal.StringToCoTaskMemUni(_arguments); return 0; }
    public int SetAdditionalBrowserArguments(nint value) { _arguments = Marshal.PtrToStringUni(value); return 0; }
    public int GetLanguage(out nint value) { value = Marshal.StringToCoTaskMemUni(_language); return 0; }
    public int SetLanguage(nint value) { _language = Marshal.PtrToStringUni(value); return 0; }
    public int GetTargetCompatibleBrowserVersion(out nint value) { value = Marshal.StringToCoTaskMemUni(_version); return 0; }
    public int SetTargetCompatibleBrowserVersion(nint value) { _version = Marshal.PtrToStringUni(value); return 0; }
    public int GetAllowSingleSignOnUsingOSPrimaryAccount(out int value) { value = _allowSingleSignOn; return 0; }
    public int SetAllowSingleSignOnUsingOSPrimaryAccount(int value) { _allowSingleSignOn = value; return 0; }
}

// The frames of a WebView2 document, each listened to for Runic's drop messages until it is destroyed or the window
// closes. Nested frames arrive through ICoreWebView2Frame7 where the runtime has it. Window thread only.
internal sealed class WebViewFrames(Action<nint> receive) : IDisposable
{
    private readonly List<Frame> _frames = [];
    private bool _disposed;

    // FrameCreated of the WebView or of a frame: the event arguments hold the new frame.
    internal void Created(nint args)
    {
        if (_disposed) return;
        var frame = WindowsWebView2Interop.GetPointer(args, WindowsWebView2Interop.FrameCreatedFrameSlot);
        if (frame == 0) return;
        try
        {
            nint frame2;
            try { frame2 = WindowsWebView2Interop.Query(frame, WindowsWebView2Interop.Frame2Interface); }
            catch (InvalidCastException) { return; } // A runtime without frame messages.
            var entry = new Frame(frame2);
            _frames.Add(entry);
            entry.Add(frame2, WindowsWebView2Interop.FrameWebMessageReceivedSlot, new WebViewComReference<IWebViewFrameWebMessageReceived>(new WebViewEvent(receive)));
            entry.Add(frame2, WindowsWebView2Interop.FrameDestroyedSlot, new WebViewComReference<IWebViewFrameDestroyed>(new WebViewFrameDestroyed(() => Destroyed(entry))));
            try
            {
                entry.Nested = WindowsWebView2Interop.Query(frame, WindowsWebView2Interop.Frame7Interface);
                entry.Add(entry.Nested, WindowsWebView2Interop.FrameNestedFrameCreatedSlot, new WebViewComReference<IWebViewFrameCreated>(new WebViewEvent(Created)));
            }
            catch (InvalidCastException) { }
        }
        finally
        {
            WindowsWebView2Interop.Release(frame);
        }
    }

    private void Destroyed(Frame entry)
    {
        if (_frames.Remove(entry)) entry.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var entry in _frames) entry.Dispose();
        _frames.Clear();
    }

    private sealed class Frame(nint frame2) : IDisposable
    {
        private readonly List<(nint Source, int Slot, long Token, IDisposable Handler)> _events = [];
        internal nint Nested { get; set; }

        internal void Add<T>(nint source, int slot, WebViewComReference<T> handler) where T : class
        {
            try { _events.Add((source, slot, WindowsWebView2Interop.AddEvent(source, slot, handler.Pointer), handler)); }
            catch { handler.Dispose(); throw; }
        }

        // A destroyed frame may refuse the removal; its handlers are released either way.
        public void Dispose()
        {
            foreach (var entry in _events)
            {
                try { WindowsWebView2Interop.RemoveEvent(entry.Source, entry.Slot + 1, entry.Token); }
                catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException) { }
                entry.Handler.Dispose();
            }
            _events.Clear();
            WindowsWebView2Interop.Release(Nested);
            Nested = 0;
            WindowsWebView2Interop.Release(frame2);
        }
    }
}

[GeneratedComClass]
internal sealed partial class WebViewFrameDestroyed(Action destroyed) : IWebViewFrameDestroyed
{
    public int Invoke(nint sender, nint args)
    {
        try { destroyed(); return 0; }
        catch (Exception exception) { return Marshal.GetHRForException(exception); }
    }
}
