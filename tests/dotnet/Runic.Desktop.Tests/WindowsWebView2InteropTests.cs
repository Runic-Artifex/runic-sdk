using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

public sealed partial class WindowsWebView2InteropTests
{
    [Fact]
    public void VtableSlotsMatchThePinnedMicrosoftSdkHeader()
    {
        var header = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "interop", "WebView2.h"));
        foreach (var (type, method, slot) in new[]
        {
            ("ICoreWebView2", "Navigate", WindowsWebView2Interop.NavigateSlot),
            ("ICoreWebView2", "add_PermissionRequested", WindowsWebView2Interop.PermissionRequestedSlot),
            ("ICoreWebView2", "add_DocumentTitleChanged", WindowsWebView2Interop.DocumentTitleChangedSlot),
            ("ICoreWebView2", "get_DocumentTitle", WindowsWebView2Interop.DocumentTitleSlot),
            ("ICoreWebView2", "add_WindowCloseRequested", WindowsWebView2Interop.WindowCloseRequestedSlot),
            ("ICoreWebView2Controller", "put_IsVisible", WindowsWebView2Interop.ControllerVisibleSlot),
            ("ICoreWebView2Controller", "put_Bounds", WindowsWebView2Interop.ControllerBoundsSlot),
            ("ICoreWebView2Controller", "MoveFocus", WindowsWebView2Interop.ControllerFocusSlot),
            ("ICoreWebView2Controller", "Close", WindowsWebView2Interop.ControllerCloseSlot),
            ("ICoreWebView2Controller", "get_CoreWebView2", WindowsWebView2Interop.ControllerWebViewSlot),
            ("ICoreWebView2Controller2", "put_DefaultBackgroundColor", WindowsWebView2Interop.ControllerBackgroundSlot),
            ("ICoreWebView2Environment", "CreateCoreWebView2Controller", WindowsWebView2Interop.EnvironmentCreateControllerSlot),
            ("ICoreWebView2PermissionRequestedEventArgs", "get_PermissionKind", WindowsWebView2Interop.PermissionKindSlot),
            ("ICoreWebView2PermissionRequestedEventArgs", "put_State", WindowsWebView2Interop.PermissionStateSlot),
            ("ICoreWebView2PermissionRequestedEventArgs2", "put_Handled", WindowsWebView2Interop.PermissionHandledSlot),
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

    [Fact]
    public void DesktopDoesNotReferenceTheManagedWebView2Wrapper()
        => Assert.DoesNotContain(typeof(DesktopHost).Assembly.GetReferencedAssemblies(),
            name => name.Name?.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal) == true);

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
