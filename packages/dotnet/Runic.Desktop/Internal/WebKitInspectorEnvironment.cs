using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// #1028, D066.38 (3): WebKitGTK opens its remote inspector when WEBKIT_INSPECTOR_SERVER or WEBKIT_INSPECTOR_HTTP_SERVER is
// set as its first web context starts, whatever enable-developer-extras says, so DesktopWebContentPolicy.DevTools = false
// does not stop it. Outside Development the GTK hosts remove both from the process environment before they create a web
// context. WebKit reads the native environment (g_getenv), which Environment.SetEnvironmentVariable does not change on
// Unix, so both copies are cleared: the native one for WebKit and its web processes, .NET's for processes the
// application starts. In Development they stay; D066.38 (1)/(2) open the inspector there in 0.21.
internal static partial class WebKitInspectorEnvironment
{
    internal static readonly string[] Variables = ["WEBKIT_INSPECTOR_SERVER", "WEBKIT_INSPECTOR_HTTP_SERVER"];

    private static int _applied;

    // GTK thread, before each web context a GTK host creates; acts once per process, before the first.
    internal static void Apply()
    {
        if (Interlocked.Exchange(ref _applied, 1) != 0) return;
        Apply(Environment.GetEnvironmentVariable, Clear);
    }

    // The environment test of DevTools (WebContentRules.IsDevelopment): outside Development every variable is cleared.
    internal static void Apply(Func<string, string?> read, Action<string> clear)
    {
        if (WebContentRules.IsDevelopment(read)) return;
        foreach (var name in Variables) clear(name);
    }

    // unsetenv does not free the removed string in glibc or musl, so a getenv on another thread at the same time reads
    // either the old value or none.
    private static void Clear(string name)
    {
        Environment.SetEnvironmentVariable(name, null);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD()) return;
        try
        {
            _ = Unsetenv(name);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            // No C library to change: WebKit cannot run here either.
        }
    }

    [LibraryImport("libc", EntryPoint = "unsetenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Unsetenv(string name);
}
