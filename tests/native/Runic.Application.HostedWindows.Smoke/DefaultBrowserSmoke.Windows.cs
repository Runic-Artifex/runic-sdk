using System.Diagnostics;
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

    // #1301: the processes of the http association's executable (Edge's msedge.exe on the runners) that this user can
    // see, by id.
    [SupportedOSPlatform("windows")]
    private static HashSet<int> BrowserProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in BrowserProcesses())
            using (process) ids.Add(process.Id);
        return ids;
    }

    [SupportedOSPlatform("windows")]
    private static Process[] BrowserProcesses()
    {
        var executable = Query("http", 2).Trim('\'', '(', ')');
        return executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable))
            : [];
    }

    // #1301: the browser's processes of this user: each browser process (no --type) with its start and command line,
    // which are new since `before`, how many child processes (renderers, GPU, utilities) there are, and the browser's
    // visible top-level windows, whose titles show a first-run page or a dialog instead of the URL; other users'
    // processes, whose command line this user cannot read, only as a count. Whether the user's Edge profile existed,
    // and the browser's update state.
    // A diagnostic: it reports its own failure instead of failing the smoke (the heartbeat's timer would end the process).
    [SupportedOSPlatform("windows")]
    private static string BrowserState(HashSet<int> before)
    {
        try { return ReadBrowserState(before); }
        catch (Exception error) { return $"browser state unavailable: {error.GetType().Name}: {error.Message}"; }
    }

    [SupportedOSPlatform("windows")]
    private static string ReadBrowserState(HashSet<int> before)
    {
        var processes = BrowserProcesses();
        try
        {
            var ids = new HashSet<int>();
            var browsers = new List<string>();
            int children = 0, others = 0;
            foreach (var process in processes)
            {
                var line = CommandLine(process.Id);
                if (line is null)
                {
                    others++;
                    continue;
                }
                ids.Add(process.Id);
                if (line.Contains(" --type=", StringComparison.Ordinal))
                {
                    children++;
                    continue;
                }
                string started;
                try { started = process.StartTime.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { started = "?"; }
                browsers.Add($"{process.Id}{(before.Count > 0 && !before.Contains(process.Id) ? " (new)" : "")} started {started}: {line}");
            }
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data", "Local State");
            return $"{ids.Count} browser processes of this user, {children} of them children, and {others} of other users; " +
                $"browser processes [{string.Join("; ", browsers)}]; visible windows [{string.Join("; ", VisibleWindows(ids))}]; " +
                $"Edge's Local State {(File.Exists(profile) ? "exists" : "missing")}; {UpdateState()}";
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    // #1301: whether the browser is being updated: the version directories beside its executable, a staged new_msedge.exe
    // (an update that waits for the browser to restart), and running updater or installer processes.
    [SupportedOSPlatform("windows")]
    private static string UpdateState()
    {
        var executable = Query("http", 2).Trim('\'', '(', ')');
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(executable) is not { } directory) return "no update state";
        string[] versions;
        try
        {
            versions = Directory.GetDirectories(directory).Select(Path.GetFileName).Where(static name => name is { Length: > 0 } && char.IsAsciiDigit(name[0])).ToArray()!;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { versions = [$"({error.GetType().Name})"]; }
        var staged = File.Exists(Path.Combine(directory, "new_" + Path.GetFileName(executable)));
        var updaters = new List<string>();
        foreach (var name in new[] { "MicrosoftEdgeUpdate", "setup", "msedgeupdate", "elevation_service" })
        {
            foreach (var process in Process.GetProcessesByName(name))
                using (process) updaters.Add($"{name} {process.Id}");
        }
        return $"versions [{string.Join(", ", versions)}], staged update {(staged ? "present" : "absent")}, updater processes [{string.Join(", ", updaters)}]";
    }

    // A process's command line (NtQueryInformationProcess, ProcessCommandLineInformation), for a process of this user.
    [SupportedOSPlatform("windows")]
    private static unsafe string? CommandLine(int id)
    {
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, 0, (uint)id);
        if (process == 0) return null;
        try
        {
            const int Size = 64 * 1024;
            var buffer = stackalloc byte[Size];
            if (NtQueryInformationProcess(process, 60 /* ProcessCommandLineInformation */, buffer, Size, out _) < 0) return null;
            // A UNICODE_STRING whose buffer follows it.
            var length = *(ushort*)buffer;
            var text = *(char**)(buffer + IntPtr.Size);
            return new string(text, 0, length / 2);
        }
        finally { _ = CloseHandle(process); }
    }

    // The visible top-level windows of the given processes: title and class.
    [SupportedOSPlatform("windows")]
    private static unsafe List<string> VisibleWindows(HashSet<int> processes)
    {
        var found = new List<(nint Window, string Text)>();
        var state = System.Runtime.InteropServices.GCHandle.Alloc((processes, found));
        try { _ = EnumWindows(&VisibleWindow, System.Runtime.InteropServices.GCHandle.ToIntPtr(state)); }
        finally { state.Free(); }
        return found.Select(static window => window.Text).ToList();
    }

    [UnmanagedCallersOnly]
    private static unsafe int VisibleWindow(nint window, nint parameter)
    {
        var (processes, found) = ((HashSet<int>, List<(nint, string)>))System.Runtime.InteropServices.GCHandle.FromIntPtr(parameter).Target!;
        uint process;
        _ = GetWindowThreadProcessId(window, &process);
        if (!processes.Contains((int)process) || IsWindowVisible(window) == 0) return 1;
        var title = stackalloc char[256];
        var titleLength = GetWindowTextW(window, title, 256);
        var className = stackalloc char[256];
        var classLength = GetClassNameW(window, className, 256);
        found.Add((window, $"{process} '{new string(title, 0, Math.Max(titleLength, 0))}' ({new string(className, 0, Math.Max(classLength, 0))})"));
        return 1;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, int inherit, uint id);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(nint process, int kind, void* information, int length, out int returned);

    [LibraryImport("user32.dll")]
    private static unsafe partial int EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint parameter);

    [LibraryImport("user32.dll")]
    private static unsafe partial uint GetWindowThreadProcessId(nint window, uint* process);

    [LibraryImport("user32.dll")]
    private static partial int IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetWindowTextW(nint window, char* text, int length);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(nint window, char* text, int length);

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
