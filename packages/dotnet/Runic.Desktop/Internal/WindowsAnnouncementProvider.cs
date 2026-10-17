using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// The UI Automation provider of a WebView2 host window, the source of its announcements. UiaRaiseNotificationEvent on
// the host provider of a window without a provider of its own (UiaHostProviderFromHwnd) succeeds, but UI Automation
// delivers the event to no client, Narrator included; Runic.Desktop.AccessibilityProbe checks the notification on the
// Windows runners. The window answers WM_GETOBJECT with this provider. It adds no property or pattern: UI Automation
// takes those from the window's host provider, so the window's name, role and children stay as before.
internal static unsafe partial class WindowsAnnouncementProvider
{
    private const int NoInterface = unchecked((int)0x80004002);
    private const int ServerSideProvider = 0x2;
    internal const int RootObjectId = -25; // UiaRootObjectId
    private static readonly Guid Unknown = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid RawElementProviderSimple = new("d6dd68d1-86fd-4332-8666-9abedea2d24c");
    private static readonly nint* Methods = CreateMethods();

    [StructLayout(LayoutKind.Sequential)]
    private struct Provider
    {
        internal nint* Methods;
        internal int References;
        internal nint Window;
    }

    /// <summary>A provider for <paramref name="window"/> with one reference, which the window releases when destroyed.</summary>
    internal static nint Create(nint window)
    {
        var provider = (Provider*)NativeMemory.AllocZeroed((nuint)sizeof(Provider));
        provider->Methods = Methods;
        provider->References = 1;
        provider->Window = window;
        return (nint)provider;
    }

    /// <summary>Answers WM_GETOBJECT for the window's root object.</summary>
    internal static nint Return(nint window, nuint wParam, nint lParam, nint provider)
        => UiaReturnRawElementProvider(window, wParam, lParam, provider);

    /// <summary>Releases the window's provider when the window is destroyed, as UI Automation asks.</summary>
    internal static void Destroy(nint window, nint provider)
    {
        _ = UiaReturnRawElementProvider(window, 0, 0, 0);
        _ = UiaDisconnectProvider(provider);
        _ = Release(provider);
    }

    private static nint* CreateMethods()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var methods = (nint*)NativeMemory.Alloc(7, (nuint)sizeof(nint));
        methods[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        methods[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        methods[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReleaseMethod;
        // IRawElementProviderSimple: ProviderOptions, GetPatternProvider, GetPropertyValue, HostRawElementProvider.
        methods[3] = (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&GetProviderOptions;
        methods[4] = (nint)(delegate* unmanaged[Stdcall]<nint, int, nint*, int>)&GetPatternProvider;
        methods[5] = (nint)(delegate* unmanaged[Stdcall]<nint, int, byte*, int>)&GetPropertyValue;
        methods[6] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetHostRawElementProvider;
        return methods;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(nint self, Guid* iid, nint* result)
    {
        if (*iid == Unknown || *iid == RawElementProviderSimple)
        {
            Interlocked.Increment(ref ((Provider*)self)->References);
            *result = self;
            return 0;
        }
        *result = 0;
        return NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint self) => (uint)Interlocked.Increment(ref ((Provider*)self)->References);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseMethod(nint self) => Release(self);

    private static uint Release(nint self)
    {
        var references = Interlocked.Decrement(ref ((Provider*)self)->References);
        if (references == 0)
        {
            NativeMemory.Free((void*)self);
        }
        return (uint)references;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetProviderOptions(nint self, int* options)
    {
        *options = ServerSideProvider;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetPatternProvider(nint self, int pattern, nint* result)
    {
        *result = 0;
        return 0;
    }

    // VT_EMPTY: the host provider supplies every property.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetPropertyValue(nint self, int property, byte* variant)
    {
        new Span<byte>(variant, 8 + 2 * sizeof(nint)).Clear();
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetHostRawElementProvider(nint self, nint* result) => UiaHostProviderFromHwnd(((Provider*)self)->Window, result);

    [LibraryImport("uiautomationcore.dll")]
    private static partial nint UiaReturnRawElementProvider(nint window, nuint wParam, nint lParam, nint provider);

    [LibraryImport("uiautomationcore.dll")]
    private static partial int UiaDisconnectProvider(nint provider);

    [LibraryImport("uiautomationcore.dll")]
    private static partial int UiaHostProviderFromHwnd(nint window, nint* provider);
}
