using System.Runtime.InteropServices;

// UI Automation notification events (IUIAutomation5::AddNotificationEventHandler, Windows 10 1709), which Narrator
// speaks for DesktopWindow.AnnounceAsync. The managed System.Windows.Automation client does not deliver them, so the
// probe subscribes through the COM client: a handler object with a hand-made vtable, kept for the process lifetime.
internal static unsafe partial class UiaNotifications
{
    private const int TreeScopeSubtree = 7;
    private const int NoInterface = unchecked((int)0x80004002);
    private static readonly Guid AutomationClass = new("e22ad333-b25f-460c-83d0-0581107395c9"); // CUIAutomation8
    private static readonly Guid Automation5 = new("25f700c8-d816-4057-a9dc-3cbdee77e256");
    private static readonly Guid Handler = new("c7cb2637-e6c2-4d0c-85de-4948c02175c7");
    private static readonly Guid Unknown = new("00000000-0000-0000-c000-000000000046");
    private static Action<string, string>? s_received;
    private static nint s_handler;

    /// <summary>Subscribes to the notifications of the window and its descendants.</summary>
    internal static void Subscribe(nint window, Action<string, string> received)
    {
        s_received = received;
        Check(CoCreateInstance(AutomationClass, 0, 1, Automation5, out var automation), "CoCreateInstance(CUIAutomation8)");
        var vtable = *(nint**)automation;
        nint element;
        // IUIAutomation::ElementFromHandle is slot 6; IUIAutomation5::AddNotificationEventHandler slot 68.
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)vtable[6])(automation, window, &element), "ElementFromHandle");
        var methods = (nint*)NativeMemory.Alloc(4, (nuint)sizeof(nint));
        methods[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        methods[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        methods[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        methods[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int, int, nint, nint, int>)&HandleNotificationEvent;
        s_handler = (nint)NativeMemory.Alloc(1, (nuint)sizeof(nint));
        *(nint**)s_handler = methods;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint, nint, int>)vtable[68])(automation, element, TreeScopeSubtree, 0, s_handler),
            "AddNotificationEventHandler");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int QueryInterface(nint self, Guid* iid, nint* result)
    {
        if (*iid == Unknown || *iid == Handler)
        {
            *result = self;
            return 0;
        }
        *result = 0;
        return NoInterface;
    }

    // The handler lives as long as the process.
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint AddRef(nint self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint Release(nint self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int HandleNotificationEvent(nint self, nint sender, int kind, int processing, nint displayString, nint activityId)
    {
        try
        {
            s_received?.Invoke(Marshal.PtrToStringBSTR(displayString), $"kind {kind}, processing {processing}, activity {Marshal.PtrToStringBSTR(activityId)}");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("UI Automation notification failed: " + error.Message);
        }
        return 0;
    }

    private static void Check(int result, string call)
    {
        if (result < 0) throw new InvalidOperationException($"{call} failed with HRESULT 0x{result:x8}.");
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid type, nint outer, uint context, in Guid iid, out nint instance);
}
