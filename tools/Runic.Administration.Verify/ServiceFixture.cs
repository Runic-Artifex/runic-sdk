using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static unsafe partial class ServiceFixture
{
    private static readonly ManualResetEvent Stopped = new(false);
    private static nint _handle;
    private static string _name = "";
    private static int _error;
    internal static int Run(string name)
    {
        if (!name.StartsWith("RunicVerify-", StringComparison.Ordinal) || !Guid.TryParseExact(name["RunicVerify-".Length..], "N", out _)) return 2;
        _name = name;
        fixed (char* text = name)
        {
            Entry* table = stackalloc Entry[2];
            table[0] = new() { Name = text, Main = &ServiceMain };
            table[1] = default;
            if (StartServiceCtrlDispatcherW(table) == 0) return Marshal.GetLastPInvokeError();
        }
        return _error;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ServiceMain(uint count, char** arguments)
    {
        try
        {
            fixed (char* name = _name) _handle = RegisterServiceCtrlHandlerW(name, &Control);
            if (_handle == 0) { _error = Marshal.GetLastPInvokeError(); return; }
            Status(4);
            Stopped.WaitOne();
            Status(1);
        }
        catch { _error = 13; if (_handle != 0) Status(1); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Control(uint control)
    {
        try
        {
            switch (control)
            {
                case 1: case 5: Status(3); Stopped.Set(); break;
                case 2: Status(7); break;
                case 3: Status(4); break;
            }
        }
        catch { _error = 13; Stopped.Set(); }
    }
    private static void Status(uint state)
    {
        var status = new NativeStatus { Type = 0x10, State = state, Controls = state is 4 or 7 ? 7u : 0u, ExitCode = (uint)_error, WaitHint = state == 3 ? 5000u : 0u };
        if (SetServiceStatus(_handle, &status) == 0) _error = Marshal.GetLastPInvokeError();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Entry { internal char* Name; internal delegate* unmanaged[Stdcall]<uint, char**, void> Main; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStatus { internal uint Type, State, Controls, ExitCode, SpecificExitCode, CheckPoint, WaitHint; }
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int StartServiceCtrlDispatcherW(Entry* table);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial nint RegisterServiceCtrlHandlerW(char* name, delegate* unmanaged[Stdcall]<uint, void> handler);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int SetServiceStatus(nint handle, NativeStatus* status);
}
