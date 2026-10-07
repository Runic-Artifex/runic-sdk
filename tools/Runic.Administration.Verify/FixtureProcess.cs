using System.ComponentModel;
using System.Runtime.InteropServices;

// Ends the disposable service's process when the SCM cannot stop it. The handle is
// checked against this verifier's executable before termination, so a reused process
// identifier never ends an unrelated process.
internal static unsafe partial class FixtureProcess
{
    private const uint ProcessTerminate = 0x0001, ProcessQueryLimitedInformation = 0x1000, Synchronize = 0x00100000;

    internal static void Terminate(uint processId, string expectedImage)
    {
        nint handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation | Synchronize, 0, processId);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Opening the fixture service process failed.");
        try
        {
            char[] buffer = new char[32768];
            uint length = (uint)buffer.Length;
            fixed (char* text = buffer)
                if (QueryFullProcessImageNameW(handle, 0, text, &length) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Reading the fixture service process image failed.");
            string image = new(buffer, 0, (int)length);
            if (!string.Equals(Path.GetFullPath(image), Path.GetFullPath(expectedImage), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The service process is not this verifier (" + image + "); refusing to end it.");
            if (TerminateProcess(handle, 1) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Ending the fixture service process failed.");
            if (WaitForSingleObject(handle, 10_000) != 0)
                throw new TimeoutException("The fixture service process did not exit within 10 seconds.");
        }
        finally { _ = CloseHandle(handle); }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, int inherit, uint processId);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int QueryFullProcessImageNameW(nint process, uint flags, char* name, uint* size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int TerminateProcess(nint process, uint exitCode);
    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);
    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
