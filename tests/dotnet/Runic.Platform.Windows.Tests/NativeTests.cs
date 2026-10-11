using System.Diagnostics;
using System.Runtime.InteropServices;
using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// Run(args) also serves the --native-hold and --native-write peer processes, which Program.Main dispatches before
// the test platform parses arguments; the Native case runs the former --native mode through it.
internal sealed partial class NativeTests
{
    [Test, Explicit, Category("Native")]
    public void NativeClipboard()
    {
        if (Run(["--native"]) != 0) throw new InvalidOperationException("Native clipboard checks failed.");
    }

    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) throw new PlatformNotSupportedException("Native clipboard evidence requires Windows.");
        if (args[0] == "--native-hold")
        {
            if (!Win32Clipboard.Open(0)) throw new InvalidOperationException("Could not hold clipboard");
            try { Console.WriteLine("ready"); Console.Out.Flush(); Console.ReadLine(); }
            finally { Win32Clipboard.Close(); }
            return 0;
        }
        // A real HWND is required by EmptyClipboard/SetClipboardData; STATIC is a system class.
        nint window = CreateWindowExW(0, "STATIC", "Runic clipboard acceptance", 0, 0, 0, 1, 1, 0, 0, 0, 0);
        if (window == 0) throw new InvalidOperationException("Could not create clipboard owner HWND");
        try
        {
            var native = new Win32Clipboard();
            if (args[0] == "--native-write")
            {
                Assert(native.Write(window, args[1]) is PlatformResult<PlatformUnit>.Success);
                return 0;
            }
            if (args[0] != "--native") throw new ArgumentException("Unknown native mode");
            foreach (string text in new[] { "", "Runic \uD83D\uDE80 Unicode 漢字\r\nsecond line" })
            {
                using var child = Start("--native-write", text);
                try
                {
                    Assert(child.WaitForExit(15000));
                    Assert(child.ExitCode == 0);
                }
                finally { StopChild(child); }
                Assert(native.Read(window, text.Length) is PlatformResult<string?>.Success success && success.Value == text);
                if (text.Length > 0) Assert(native.Read(window, text.Length - 1) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.TooLarge });
            }
            Assert(Win32Clipboard.Open(window));
            try { Assert(Win32Clipboard.Empty()); }
            finally { Win32Clipboard.Close(); }
            Assert(native.Read(window, 0) is PlatformResult<string?>.Success { Value: null });
            using (var holder = Start("--native-hold"))
            {
                try
                {
                    Assert(holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult() == "ready");
                    Assert(native.Read(window, 10) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.ResourceBusy or PlatformFailureCode.PermissionDenied });
                    Assert(native.Write(window, "busy") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy or PlatformFailureCode.PermissionDenied });
                }
                finally
                {
                    try
                    {
                        if (!holder.HasExited) holder.StandardInput.WriteLine("release");
                        Assert(holder.WaitForExit(15000));
                    }
                    finally { StopChild(holder); }
                }
            }
            Assert(native.Write(window, "post-busy retry") is PlatformResult<PlatformUnit>.Success);
            Console.WriteLine("PASS Windows native cross-process Unicode, empty/no text, bounds, contention and retry");
            return 0;
        }
        finally { Assert(DestroyWindow(window) != 0); }
    }
    private static void StopChild(Process process)
    {
        if (process.HasExited) return;
        process.Kill(entireProcessTree: true);
        Assert(process.WaitForExit(5000));
    }
    private static Process Start(params string[] arguments)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable");
        var start = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Runic.Platform.Windows.Tests.dll"));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start clipboard peer");
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Native clipboard assertion failed"); }
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")] private static partial int DestroyWindow(nint window);
}
