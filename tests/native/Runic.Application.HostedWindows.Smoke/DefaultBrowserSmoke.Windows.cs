using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Runic.Application.Views.Desktop;
using Runic.Platform;

internal static partial class DefaultBrowserSmoke
{
    // The running user's association for a URL scheme, as the shell resolves it (AssocQueryString with
    // ASSOCF_IS_PROTOCOL): the user's choice, the ProgID, the open command and the executable, with each query's HRESULT.
    [SupportedOSPlatform("windows")]
    private static string Association(string scheme)
    {
        string? choice;
        using (var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme}\UserChoice"))
            choice = key?.GetValue("ProgId") as string;
        bool machineCommand;
        using (var key = Registry.LocalMachine.OpenSubKey($@"Software\Classes\{scheme}\shell\open\command")) machineCommand = key is not null;
        return $"{scheme} for {Environment.UserName}: UserChoice '{choice ?? "(none)"}', HKLM\\Software\\Classes\\{scheme}\\shell\\open\\command " +
            $"{(machineCommand ? "exists" : "missing")}, ProgID {Query(scheme, 20)}, command {Query(scheme, 1)}, executable {Query(scheme, 2)}";
    }

    [SupportedOSPlatform("windows")]
    private static unsafe string Query(string scheme, int kind)
    {
        const int IsProtocol = 0x1000;
        uint length = 2048;
        var buffer = stackalloc char[(int)length];
        int result = AssocQueryStringW(IsProtocol, kind, scheme, "open", buffer, &length);
        return result < 0 ? $"(0x{result:X8})" : $"'{new string(buffer)}'";
    }

    // After a failed launch: the same ShellExecuteEx (SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI, the open verb, the URI as
    // the file) without an owner window, with its last error and hInstApp, which the result does not carry.
    [SupportedOSPlatform("windows")]
    private static unsafe string ShellExecuteDiagnostic(Uri uri)
    {
        fixed (char* verb = "open")
        fixed (char* file = uri.AbsoluteUri)
        {
            var info = new ShellExecuteInfo { Size = (uint)sizeof(ShellExecuteInfo), Mask = 0x100 | 0x400, Verb = verb, File = file, Show = 1 };
            var ok = ShellExecuteExW(&info) != 0;
            var error = Marshal.GetLastPInvokeError();
            return $"ShellExecuteEx({uri}) as {Environment.UserName}: {(ok ? "succeeded" : "failed")}, last error {error}, hInstApp {info.InstApp}";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ShellExecuteInfo
    {
        internal uint Size;
        internal uint Mask;
        internal nint Window;
        internal char* Verb;
        internal char* File;
        internal char* Parameters;
        internal char* Directory;
        internal int Show;
        internal nint InstApp;
        internal nint IdList;
        internal char* Class;
        internal nint ClassKey;
        internal uint HotKey;
        internal nint IconOrMonitor;
        internal nint Process;
    }

    [LibraryImport("shell32.dll", SetLastError = true)]
    private static unsafe partial int ShellExecuteExW(ShellExecuteInfo* info);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int AssocQueryStringW(int flags, int kind, string association, string? extra, char* output, uint* length);

    // AssocIsDangerous against a type Windows registers itself: .cer (CERFile, the certificate dialog), which the shell
    // lists as dangerous and Runic's own list of program types does not name. Opening it is refused before the shell, so
    // no certificate dialog opens.
    [SupportedOSPlatform("windows")]
    private static async Task DangerousTypeAsync()
    {
        string? registered;
        using (var type = Registry.ClassesRoot.OpenSubKey(".cer")) registered = type?.GetValue("") as string;
        Console.WriteLine($"Default browser: .cer is registered as '{registered ?? "(nothing)"}'.");
        var directory = Directory.CreateTempSubdirectory("runic-dangerous-type-");
        try
        {
            var file = Path.Combine(directory.FullName, "runic-launch-smoke.cer");
            await File.WriteAllTextAsync(file, "not a certificate");
            var result = await DesktopPlatformServices.ForApplication().FileLauncher.LaunchAsync(file).AsTask()
                .WaitAsync(TestTimeouts.Scale(StepTimeout));
            if (result is not PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied })
                throw new InvalidOperationException($"LaunchAsync of a .cer file returned {result}, not PermissionDenied.");
            Console.WriteLine("PASS default browser: LaunchAsync refused a .cer file (AssocIsDangerous, a type Windows registers) with PermissionDenied.");
        }
        finally
        {
            Remove(directory);
        }
    }
}
