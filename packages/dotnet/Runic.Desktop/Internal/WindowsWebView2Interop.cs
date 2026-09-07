using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Runic.Desktop.Internal;

// The stable Win32 ABI in the pinned SDK's build/native/include/WebView2.h.
// Only native pointers cross this boundary; no runtime-generated COM wrappers
// or assembly-location discovery are needed by NativeAOT applications.
internal static class WindowsWebView2Interop
{
    internal const int NavigateSlot = 5;
    internal const int PermissionRequestedSlot = 23;
    internal const int DocumentTitleChangedSlot = 46;
    internal const int DocumentTitleSlot = 48;
    internal const int WindowCloseRequestedSlot = 59;
    internal const int ControllerVisibleSlot = 4;
    internal const int ControllerBoundsSlot = 6;
    internal const int ControllerFocusSlot = 12;
    internal const int ControllerCloseSlot = 24;
    internal const int ControllerWebViewSlot = 25;
    internal const int ControllerBackgroundSlot = 27;
    internal const int EnvironmentCreateControllerSlot = 3;
    internal const int PermissionKindSlot = 4;
    internal const int PermissionStateSlot = 7;
    internal const int PermissionHandledSlot = 10;
    private static readonly Lazy<nint> Loader = new(LoadLoader);

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
        var getVersion = (delegate* unmanaged[Stdcall]<char*, nint*, int>)NativeLibrary.GetExport(
            Loader.Value, "GetAvailableCoreWebView2BrowserVersionString");
        try { return getVersion(null, &version) >= 0 && version != 0; }
        finally { Marshal.FreeCoTaskMem(version); }
    }

    internal static unsafe void CreateEnvironment(string? profile, nint options, nint callback)
    {
        var create = (delegate* unmanaged[Stdcall]<char*, char*, nint, nint, int>)NativeLibrary.GetExport(
            Loader.Value, "CreateCoreWebView2EnvironmentWithOptions");
        fixed (char* profilePath = profile) Marshal.ThrowExceptionForHR(create(null, profilePath, options, callback));
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

    internal static unsafe void SetInteger(nint instance, int slot, int value)
        => Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(instance, slot))(instance, value));

    internal static unsafe void Navigate(nint webView, string url)
    {
        fixed (char* value = url)
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(webView, NavigateSlot))(webView, value));
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
    private readonly List<(int Slot, long Token, IDisposable Handler)> _events = [];

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
        using var environmentOptions = new WebViewComReference<IWebViewEnvironmentOptions>(new WebViewEnvironmentOptions(options.CustomParameters));
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
    internal void MoveFocus() => WindowsWebView2Interop.SetInteger(_controller, WindowsWebView2Interop.ControllerFocusSlot, 0);
    internal void Navigate(string url) => WindowsWebView2Interop.Navigate(_webView, url);
    internal void SetTransparent()
    {
        var controller2 = WindowsWebView2Interop.Query(_controller, new Guid("c979903e-d4ca-4228-92eb-47ee3fa96eab"));
        // COREWEBVIEW2_COLOR is four bytes in A/R/G/B order; transparent is all zero.
        try { WindowsWebView2Interop.SetInteger(controller2, WindowsWebView2Interop.ControllerBackgroundSlot, 0); }
        finally { WindowsWebView2Interop.Release(controller2); }
    }

    internal void RegisterEvents(Action<string> titleChanged, Action closeRequested, DesktopPermissionGrant permissions)
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
            var kind = WindowsWebView2Interop.GetInteger(args, WindowsWebView2Interop.PermissionKindSlot);
            var allowed = kind is 1 or 2 && (permissions & DesktopPermissionGrant.MediaCapture) != 0;
            WindowsWebView2Interop.SetInteger(args, WindowsWebView2Interop.PermissionStateSlot, allowed ? 1 : 2);
            var args2 = WindowsWebView2Interop.Query(args, new Guid("74d7127f-9de6-4200-8734-42d6fb4ff741"));
            try { WindowsWebView2Interop.SetInteger(args2, WindowsWebView2Interop.PermissionHandledSlot, 1); }
            finally { WindowsWebView2Interop.Release(args2); }
        }));
    }

    private void AddEvent<T>(int slot, T handler) where T : class
    {
        var reference = new WebViewComReference<T>(handler);
        try { _events.Add((slot, WindowsWebView2Interop.AddEvent(_webView, slot, reference.Pointer), reference)); }
        catch { reference.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_controller == 0) return;
        // Dispose on the owning STA, before its message loop stops.
        try
        {
            foreach (var entry in _events) WindowsWebView2Interop.RemoveEvent(_webView, entry.Slot + 1, entry.Token);
        }
        finally
        {
            try { WindowsWebView2Interop.Close(_controller); }
            finally
            {
                foreach (var entry in _events) entry.Handler.Dispose();
                _events.Clear();
                WindowsWebView2Interop.Release(_webView);
                WindowsWebView2Interop.Release(_controller);
                WindowsWebView2Interop.Release(_environment);
                _webView = _controller = _environment = 0;
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
[GeneratedComInterface, Guid("15e1c6a3-c72a-4df3-91d7-d097fbec6bfd")]
internal partial interface IWebViewPermissionRequested { [PreserveSig] int Invoke(nint sender, nint args); }

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

[GeneratedComClass]
internal sealed partial class WebViewEvent(Action<nint> callback) : IWebViewTitleChanged, IWebViewCloseRequested, IWebViewPermissionRequested
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
internal sealed partial class WebViewEnvironmentOptions(string? arguments) : IWebViewEnvironmentOptions
{
    private string? _arguments = arguments;
    private string? _language;
    private string? _version = "150.0.4078.44"; // CORE_WEBVIEW_TARGET_PRODUCT_VERSION in the pinned SDK.
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
