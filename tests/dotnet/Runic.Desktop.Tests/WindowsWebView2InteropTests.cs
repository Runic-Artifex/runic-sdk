using TUnit.Core;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

public sealed partial class WindowsWebView2InteropTests
{
    [Test]
    public void VtableSlotsMatchThePinnedMicrosoftSdkHeader()
    {
        var header = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "interop", "WebView2.h"));
        foreach (var (type, method, slot) in new[]
        {
            ("ICoreWebView2", "Navigate", WindowsWebView2Interop.NavigateSlot),
            ("ICoreWebView2", "add_PermissionRequested", WindowsWebView2Interop.PermissionRequestedSlot),
            ("ICoreWebView2", "AddScriptToExecuteOnDocumentCreated", WindowsWebView2Interop.AddScriptToExecuteOnDocumentCreatedSlot),
            ("ICoreWebView2", "add_DocumentTitleChanged", WindowsWebView2Interop.DocumentTitleChangedSlot),
            ("ICoreWebView2", "get_DocumentTitle", WindowsWebView2Interop.DocumentTitleSlot),
            ("ICoreWebView2", "add_WindowCloseRequested", WindowsWebView2Interop.WindowCloseRequestedSlot),
            ("ICoreWebView2Controller", "put_IsVisible", WindowsWebView2Interop.ControllerVisibleSlot),
            ("ICoreWebView2Controller", "put_Bounds", WindowsWebView2Interop.ControllerBoundsSlot),
            ("ICoreWebView2Controller", "MoveFocus", WindowsWebView2Interop.ControllerFocusSlot),
            ("ICoreWebView2Controller", "Close", WindowsWebView2Interop.ControllerCloseSlot),
            ("ICoreWebView2Controller", "get_ZoomFactor", WindowsWebView2Interop.ControllerZoomFactorSlot),
            ("ICoreWebView2Controller", "add_AcceleratorKeyPressed", WindowsWebView2Interop.ControllerAcceleratorKeyPressedSlot),
            ("ICoreWebView2Controller", "remove_AcceleratorKeyPressed", WindowsWebView2Interop.ControllerAcceleratorKeyPressedSlot + 1),
            ("ICoreWebView2AcceleratorKeyPressedEventArgs", "get_KeyEventKind", WindowsWebView2Interop.AcceleratorKeyKindSlot),
            ("ICoreWebView2AcceleratorKeyPressedEventArgs", "get_VirtualKey", WindowsWebView2Interop.AcceleratorVirtualKeySlot),
            ("ICoreWebView2AcceleratorKeyPressedEventArgs2", "put_IsBrowserAcceleratorKeyEnabled", WindowsWebView2Interop.AcceleratorBrowserKeyEnabledSlot),
            ("ICoreWebView2Controller", "get_CoreWebView2", WindowsWebView2Interop.ControllerWebViewSlot),
            ("ICoreWebView2Controller2", "put_DefaultBackgroundColor", WindowsWebView2Interop.ControllerBackgroundSlot),
            ("ICoreWebView2Environment", "CreateCoreWebView2Controller", WindowsWebView2Interop.EnvironmentCreateControllerSlot),
            ("ICoreWebView2Environment", "get_BrowserVersionString", WindowsWebView2Interop.EnvironmentBrowserVersionSlot),
            ("ICoreWebView2PermissionRequestedEventArgs", "get_Uri", WindowsWebView2Interop.PermissionUriSlot),
            ("ICoreWebView2PermissionRequestedEventArgs", "get_PermissionKind", WindowsWebView2Interop.PermissionKindSlot),
            ("ICoreWebView2PermissionRequestedEventArgs", "put_State", WindowsWebView2Interop.PermissionStateSlot),
            ("ICoreWebView2PermissionRequestedEventArgs2", "put_Handled", WindowsWebView2Interop.PermissionHandledSlot),
            ("ICoreWebView2", "add_WebMessageReceived", WindowsWebView2Interop.WebMessageReceivedSlot),
            ("ICoreWebView2", "remove_WebMessageReceived", WindowsWebView2Interop.WebMessageReceivedSlot + 1),
            ("ICoreWebView2WebMessageReceivedEventArgs", "get_Source", WindowsWebView2Interop.WebMessageSourceSlot),
            ("ICoreWebView2WebMessageReceivedEventArgs", "TryGetWebMessageAsString", WindowsWebView2Interop.WebMessageAsStringSlot),
            ("ICoreWebView2WebMessageReceivedEventArgs2", "get_AdditionalObjects", WindowsWebView2Interop.WebMessageAdditionalObjectsSlot),
            ("ICoreWebView2ObjectCollectionView", "get_Count", WindowsWebView2Interop.ObjectCollectionCountSlot),
            ("ICoreWebView2ObjectCollectionView", "GetValueAtIndex", WindowsWebView2Interop.ObjectCollectionValueSlot),
            ("ICoreWebView2File", "get_Path", WindowsWebView2Interop.FilePathSlot),
            ("ICoreWebView2", "get_Settings", WindowsWebView2Interop.SettingsSlot),
            ("ICoreWebView2", "add_NavigationStarting", WindowsWebView2Interop.NavigationStartingSlot),
            ("ICoreWebView2", "add_NavigationCompleted", WindowsWebView2Interop.NavigationCompletedSlot),
            ("ICoreWebView2", "remove_NavigationCompleted", WindowsWebView2Interop.NavigationCompletedSlot + 1),
            ("ICoreWebView2", "add_ProcessFailed", WindowsWebView2Interop.ProcessFailedSlot),
            ("ICoreWebView2", "remove_ProcessFailed", WindowsWebView2Interop.ProcessFailedSlot + 1),
            ("ICoreWebView2NavigationCompletedEventArgs", "get_IsSuccess", WindowsWebView2Interop.NavigationCompletedIsSuccessSlot),
            ("ICoreWebView2NavigationCompletedEventArgs", "get_WebErrorStatus", WindowsWebView2Interop.NavigationCompletedWebErrorStatusSlot),
            ("ICoreWebView2ProcessFailedEventArgs", "get_ProcessFailedKind", WindowsWebView2Interop.ProcessFailedKindSlot),
            ("ICoreWebView2", "GetDevToolsProtocolEventReceiver", WindowsWebView2Interop.GetDevToolsProtocolEventReceiverSlot),
            ("ICoreWebView2DevToolsProtocolEventReceiver", "add_DevToolsProtocolEventReceived", WindowsWebView2Interop.DevToolsProtocolEventReceivedSlot),
            ("ICoreWebView2DevToolsProtocolEventReceiver", "remove_DevToolsProtocolEventReceived", WindowsWebView2Interop.DevToolsProtocolEventReceivedSlot + 1),
            ("ICoreWebView2DevToolsProtocolEventReceivedEventArgs", "get_ParameterObjectAsJson", WindowsWebView2Interop.DevToolsProtocolEventParametersSlot),
            ("ICoreWebView2", "remove_NavigationStarting", WindowsWebView2Interop.NavigationStartingSlot + 1),
            ("ICoreWebView2", "add_NewWindowRequested", WindowsWebView2Interop.NewWindowRequestedSlot),
            ("ICoreWebView2", "remove_NewWindowRequested", WindowsWebView2Interop.NewWindowRequestedSlot + 1),
            ("ICoreWebView2_4", "add_DownloadStarting", WindowsWebView2Interop.DownloadStartingSlot),
            ("ICoreWebView2_4", "remove_DownloadStarting", WindowsWebView2Interop.DownloadStartingSlot + 1),
            ("ICoreWebView2Settings", "put_AreDevToolsEnabled", WindowsWebView2Interop.SettingsDevToolsSlot),
            ("ICoreWebView2Settings", "get_AreDevToolsEnabled", WindowsWebView2Interop.SettingsGetDevToolsSlot),
            ("ICoreWebView2NavigationStartingEventArgs", "get_Uri", WindowsWebView2Interop.NavigationStartingUriSlot),
            ("ICoreWebView2NavigationStartingEventArgs", "get_IsUserInitiated", WindowsWebView2Interop.NavigationStartingUserInitiatedSlot),
            ("ICoreWebView2NavigationStartingEventArgs", "put_Cancel", WindowsWebView2Interop.NavigationStartingCancelSlot),
            ("ICoreWebView2NewWindowRequestedEventArgs", "get_Uri", WindowsWebView2Interop.NewWindowUriSlot),
            ("ICoreWebView2NewWindowRequestedEventArgs", "put_Handled", WindowsWebView2Interop.NewWindowHandledSlot),
            ("ICoreWebView2NewWindowRequestedEventArgs", "get_IsUserInitiated", WindowsWebView2Interop.NewWindowUserInitiatedSlot),
            ("ICoreWebView2DownloadStartingEventArgs", "put_Cancel", WindowsWebView2Interop.DownloadStartingCancelSlot),
            ("ICoreWebView2_13", "get_Profile", WindowsWebView2Interop.WebView13ProfileSlot),
            ("ICoreWebView2Profile", "get_PreferredColorScheme", WindowsWebView2Interop.ProfileGetPreferredColorSchemeSlot),
            ("ICoreWebView2Profile", "put_PreferredColorScheme", WindowsWebView2Interop.ProfilePreferredColorSchemeSlot),
        })
        {
            var start = header.IndexOf($"typedef struct {type}Vtbl", StringComparison.Ordinal);
            Assert.True(start >= 0, type);
            var end = header.IndexOf("END_INTERFACE", start, StringComparison.Ordinal);
            var methods = AbiMethodPattern().Matches(header[start..end]).Select(match => match.Groups[1].Value).ToArray();
            Assert.Equal(method, methods[slot]);
        }
    }

    [GeneratedRegex(@"STDMETHODCALLTYPE \*(\w+)")]
    private static partial Regex AbiMethodPattern();

    [Test]
    public void DesktopDoesNotReferenceTheManagedWebView2Wrapper()
        => Assert.DoesNotContain(typeof(DesktopHost).Assembly.GetReferencedAssemblies(),
            name => name.Name?.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal) == true);

    [Test]
    public unsafe void GeneratedOptionsExposeTheNativeStringAndBooleanAbi()
    {
        using var options = new WebViewComReference<IWebViewEnvironmentOptions>(new WebViewEnvironmentOptions("--test-flag"));
        var pointer = WindowsWebView2Interop.Query(options.Pointer, typeof(IWebViewEnvironmentOptions).GUID);
        try
        {
            var arguments = WindowsWebView2Interop.GetPointer(pointer, 3);
            try { Assert.Equal("--test-flag", Marshal.PtrToStringUni(arguments)); }
            finally { Marshal.FreeCoTaskMem(arguments); }
            Assert.Equal(0, WindowsWebView2Interop.GetInteger(pointer, 9));
            WindowsWebView2Interop.SetInteger(pointer, 10, 1);
            Assert.Equal(1, WindowsWebView2Interop.GetInteger(pointer, 9));
        }
        finally { WindowsWebView2Interop.Release(pointer); }
    }

    [Test]
    public unsafe void GeneratedEventsDispatchThroughIUnknownAndReturnFailuresAsHresults()
    {
        nint received = 0;
        using var handler = new WebViewComReference<IWebViewPermissionRequested>(new WebViewEvent(args => received = args));
        var invoke = (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)WindowsWebView2Interop.Slot(handler.Pointer, 3);
        Assert.Equal(0, invoke(handler.Pointer, 0, 42));
        Assert.Equal((nint)42, received);

        using var failing = new WebViewComReference<IWebViewCloseRequested>(new WebViewEvent(_ => throw new InvalidOperationException("test")));
        var invokeFailure = (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)WindowsWebView2Interop.Slot(failing.Pointer, 3);
        Assert.True(invokeFailure(failing.Pointer, 0, 0) < 0);
    }

    [Test]
    public async Task CompletionOwnsExactlyOneReferenceToTheBorrowedNativeResult()
    {
        var (completed, expected) = ExerciseCompletionOwnership();
        Assert.Equal(expected, await completed);
    }

    private static unsafe (Task<nint> Completed, nint Expected) ExerciseCompletionOwnership()
    {
        // A tiny native IUnknown makes reference ownership observable without Windows.
        var vtable = stackalloc nint[3];
        vtable[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        vtable[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        var result = new ReferenceCounted { Vtable = vtable, References = 1 };
        var completion = new WebViewCompletion();
        using var handler = new WebViewComReference<IWebViewEnvironmentCompleted>(completion);
        var invoke = (delegate* unmanaged[Stdcall]<nint, int, nint, int>)WindowsWebView2Interop.Slot(handler.Pointer, 3);
        Assert.Equal(0, invoke(handler.Pointer, 0, (nint)(&result)));
        Assert.True(completion.Task.IsCompletedSuccessfully);
        var expected = (nint)(&result);
        Assert.Equal(2, result.References);
        // A duplicate completion must not leak another native reference.
        invoke(handler.Pointer, 0, (nint)(&result));
        Assert.Equal(2, result.References);
        WindowsWebView2Interop.Release(expected);
        Assert.Equal(1, result.References);
        return (completion.Task, expected);
    }

    [Test]
    public void FileDropInterfacesMatchThePinnedMicrosoftSdkHeader()
    {
        var header = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "interop", "WebView2.h"));
        Guid Interface(string name) => new(new Regex($@"MIDL_INTERFACE\(""([0-9a-f-]+)""\)\s+{name}\b").Match(header).Groups[1].Value);
        Assert.Equal(Interface("ICoreWebView2WebMessageReceivedEventHandler"), typeof(IWebViewWebMessageReceived).GUID);
        Assert.Equal(Interface("ICoreWebView2WebMessageReceivedEventArgs2"), WindowsWebView2Interop.WebMessageReceivedEventArgs2);
        Assert.Equal(Interface("ICoreWebView2File"), WindowsWebView2Interop.FileInterface);
    }

    [Test]
    public void StartupProgressInterfacesMatchThePinnedMicrosoftSdkHeader()
    {
        var header = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "interop", "WebView2.h"));
        Guid Interface(string name) => new(new Regex($@"MIDL_INTERFACE\(""([0-9a-f-]+)""\)\s+{name}\b").Match(header).Groups[1].Value);
        Assert.Equal(Interface("ICoreWebView2NavigationCompletedEventHandler"), typeof(IWebViewNavigationCompleted).GUID);
        Assert.Equal(Interface("ICoreWebView2ProcessFailedEventHandler"), typeof(IWebViewProcessFailed).GUID);
        Assert.Equal(Interface("ICoreWebView2DevToolsProtocolEventReceivedEventHandler"), typeof(IWebViewDevToolsEventReceived).GUID);
    }

    [Test]
    public void ColorSchemeMatchesThePinnedMicrosoftSdkHeaderAndDwm()
    {
        var header = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "interop", "WebView2.h"));
        Guid Interface(string name) => new(new Regex($@"MIDL_INTERFACE\(""([0-9a-f-]+)""\)\s+{name}\b").Match(header).Groups[1].Value);
        Assert.Equal(Interface("ICoreWebView2_13"), WindowsWebView2Interop.WebView13Interface);
        int Constant(string name) => int.Parse(new Regex($@"\b{name}\s*=\s*(\d+)").Match(header).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(Constant("COREWEBVIEW2_PREFERRED_COLOR_SCHEME_AUTO"), WindowsWebView2Interop.PreferredColorScheme(Runic.Platform.DesktopColorScheme.NoPreference));
        // LIGHT and DARK are declared as AUTO + 1 and LIGHT + 1.
        Assert.Contains("COREWEBVIEW2_PREFERRED_COLOR_SCHEME_LIGHT\t= ( COREWEBVIEW2_PREFERRED_COLOR_SCHEME_AUTO + 1 )", header);
        Assert.Contains("COREWEBVIEW2_PREFERRED_COLOR_SCHEME_DARK\t= ( COREWEBVIEW2_PREFERRED_COLOR_SCHEME_LIGHT + 1 )", header);
        Assert.Equal(1, WindowsWebView2Interop.PreferredColorScheme(Runic.Platform.DesktopColorScheme.Light));
        Assert.Equal(2, WindowsWebView2Interop.PreferredColorScheme(Runic.Platform.DesktopColorScheme.Dark));
        // DWMWA_USE_IMMERSIVE_DARK_MODE; a window without a scheme keeps Windows' default title bar.
        Assert.Equal(20, WindowsWebView2Host.DwmUseImmersiveDarkMode);
        Assert.Equal(1, WindowsWebView2Host.ImmersiveDarkMode(Runic.Platform.DesktopColorScheme.Dark));
        Assert.Equal(0, WindowsWebView2Host.ImmersiveDarkMode(Runic.Platform.DesktopColorScheme.Light));
        Assert.Null(WindowsWebView2Host.ImmersiveDarkMode(Runic.Platform.DesktopColorScheme.NoPreference));
    }

    [Test]
    public void MenuKeyInterfacesMatchThePinnedMicrosoftSdkHeader()
    {
        var header = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "interop", "WebView2.h"));
        Guid Interface(string name) => new(new Regex($@"MIDL_INTERFACE\(""([0-9a-f-]+)""\)\s+{name}\b").Match(header).Groups[1].Value);
        Assert.Equal(Interface("ICoreWebView2AcceleratorKeyPressedEventHandler"), typeof(IWebViewAcceleratorKeyPressed).GUID);
        Assert.Equal(Interface("ICoreWebView2AcceleratorKeyPressedEventArgs2"), WindowsWebView2Interop.AcceleratorKeyPressedEventArgs2);
    }

    [Test]
    public async Task DocumentScriptCompletionIgnoresTheBorrowedIdentifierAndReportsFailures()
    {
        var handlerGuid = new Regex(@"MIDL_INTERFACE\(""([0-9a-f-]+)""\)\s+ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler\b")
            .Match(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "interop", "WebView2.h")));
        Assert.Equal(new Guid(handlerGuid.Groups[1].Value), typeof(IWebViewScriptAdded).GUID);

        var added = new WebViewScriptCompletion();
        using (var handler = new WebViewComReference<IWebViewScriptAdded>(added))
        {
            var invoke = ExerciseScriptHandler(handler.Pointer);
            Assert.Equal(0, invoke);
        }
        await added.Task;

        var rejected = new WebViewScriptCompletion();
        rejected.Invoke(unchecked((int)0x80070057), 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.Task);
    }

    private static unsafe int ExerciseScriptHandler(nint handler)
    {
        fixed (char* id = "{script}")
            return ((delegate* unmanaged[Stdcall]<nint, int, nint, int>)WindowsWebView2Interop.Slot(handler, 3))(handler, 0, (nint)id);
    }

    [Test]
    public async Task FailedAndNullCompletionsFailWithoutDereferencingTheResult()
    {
        var failed = new WebViewCompletion();
        failed.Invoke(unchecked((int)0x80004005), 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.Task);
        var missing = new WebViewCompletion();
        missing.Invoke(0, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.Task);
    }

    private unsafe struct ReferenceCounted
    {
        public nint* Vtable;
        public int References;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe uint AddRef(nint value) => (uint)++((ReferenceCounted*)value)->References;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe uint Release(nint value) => (uint)--((ReferenceCounted*)value)->References;
}
