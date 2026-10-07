using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static unsafe partial class ServiceFixture
{
    private static readonly ManualResetEvent Stopped = new(false);
    private static nint _handle;
    private static string _name = "";
    private static int _error;
    // Opt-in diagnostics: create a "service-diagnostics" directory next to the executable
    // and each fixture service appends its dispatcher, status and control events there.
    private static string? _diagnostics;
    internal static int Run(string name)
    {
        if (!name.StartsWith("RunicVerify-", StringComparison.Ordinal) || !Guid.TryParseExact(name["RunicVerify-".Length..], "N", out _)) return 2;
        _name = name;
        var directory = Path.Combine(AppContext.BaseDirectory, "service-diagnostics");
        if (Directory.Exists(directory)) _diagnostics = Path.Combine(directory, name + ".log");
        fixed (char* text = name)
        {
            Entry* table = stackalloc Entry[2];
            table[0] = new() { Name = text, Main = &ServiceMain };
            table[1] = default;
            Log("dispatcher start");
            int result = StartServiceCtrlDispatcherW(table);
            int error = Marshal.GetLastPInvokeError();
            Log("dispatcher returned " + result + "; last error " + error);
            if (result == 0) return error;
        }
        return _error;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ServiceMain(uint count, char** arguments)
    {
        try
        {
            Log("ServiceMain entered");
            fixed (char* name = _name) _handle = RegisterServiceCtrlHandlerW(name, &Control);
            Log("control handler registered: " + (_handle != 0) + "; last error " + Marshal.GetLastPInvokeError());
            if (_handle == 0) { _error = Marshal.GetLastPInvokeError(); return; }
            Status(4);
            Stopped.WaitOne();
            Log("stop signalled");
            Status(1);
        }
        catch { _error = 13; if (_handle != 0) Status(1); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Control(uint control)
    {
        Log("control " + control);
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
        int result = SetServiceStatus(_handle, &status);
        if (result == 0) _error = Marshal.GetLastPInvokeError();
        Log("SetServiceStatus state " + state + " returned " + result + "; error " + _error);
    }
    private static void Log(string message)
    {
        if (_diagnostics is null) return;
        try
        {
            File.AppendAllText(_diagnostics, DateTime.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
                + " thread " + Environment.CurrentManagedThreadId + " " + message + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
