using System.Diagnostics;
using System.Runtime.InteropServices;
using Runic.Platform;

// #1174 on macOS, after the default browser: an AppleScript applet (osacompile) whose open location handler appends the
// URL to a record, declared for mailto in its Info.plist, registered with Launch Services (lsregister) and made the
// mailto default with LSSetDefaultHandlerForURLScheme. The smoke reports the default before and after the registration
// (MACOS REGISTRATION), so a refused registration is told apart from a failed launch, and restores the previous default.
internal static partial class DefaultBrowserSmoke
{
    private const string BundleId = "eu.runic-artifex.launch-smoke";
    private const string LaunchServices =
        "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";

    // The applet as the mailto default, if macOS lets the smoke make it so: then a launched mailto URI must reach it.
    // Either way the outcome is reported; the default browser above already required NSWorkspace openURL:.
    private static async Task MacMailtoHandlerAsync(IDesktopFileLauncher launcher)
    {
        var root = Directory.CreateTempSubdirectory("runic-default-handler-");
        var record = Path.Combine(root.FullName, "recorded.txt");
        var app = Path.Combine(root.FullName, "RunicLaunchSmoke.app");
        var previous = DefaultMailtoHandler();
        Console.WriteLine($"Default handler: the mailto default before the registration is '{previous ?? "(none)"}'.");
        var registered = false;
        try
        {
            await Task.Run(() =>
            {
                var script = Path.Combine(root.FullName, "handler.applescript");
                File.WriteAllText(script, $$"""
                    on open location theURL
                        do shell script "printf '%s\\n' " & quoted form of theURL & " >> " & quoted form of "{{record}}"
                    end open location
                    """);
                Tool("osacompile", "-o", app, script);
                var plist = Path.Combine(app, "Contents", "Info.plist");
                Tool("/usr/libexec/PlistBuddy", "-c", $"Set :CFBundleIdentifier {BundleId}", plist);
                Tool("/usr/libexec/PlistBuddy", "-c", "Add :CFBundleURLTypes array", plist);
                Tool("/usr/libexec/PlistBuddy", "-c", "Add :CFBundleURLTypes:0 dict", plist);
                Tool("/usr/libexec/PlistBuddy", "-c", "Add :CFBundleURLTypes:0:CFBundleURLName string Runic launch smoke", plist);
                Tool("/usr/libexec/PlistBuddy", "-c", "Add :CFBundleURLTypes:0:CFBundleURLSchemes array", plist);
                Tool("/usr/libexec/PlistBuddy", "-c", "Add :CFBundleURLTypes:0:CFBundleURLSchemes:0 string mailto", plist);
                Tool("codesign", "--force", "--sign", "-", app);
                Tool(LaunchServices, "-f", app);
            });
            var status = SetDefaultMailtoHandler(BundleId);
            var after = DefaultMailtoHandler();
            registered = status == 0 && string.Equals(after, BundleId, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"MACOS REGISTRATION: LSSetDefaultHandlerForURLScheme(mailto, {BundleId}) returned {status}; the mailto default is now " +
                $"'{after ?? "(none)"}': {(registered ? "registered" : "NOT registered")}.");
            if (!registered) return;
            var uri = Mailto("macos");
            await LaunchAsync(launcher, uri, "the application's file launcher");
            await ExpectAsync(record, uri, "the application's file launcher (NSWorkspace openURL: to the mailto default)");
        }
        finally
        {
            if (registered && previous is not null && !string.Equals(previous, BundleId, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"Default handler: restoring '{previous}' returned {SetDefaultMailtoHandler(previous)}.");
            try { Tool(LaunchServices, "-u", app); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            Remove(root);
        }
    }

    private static void Tool(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{file} did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60))) process.Kill();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', args)} exited with {process.ExitCode}: {output.Result}{error.Result}");
    }

    private static int SetDefaultMailtoHandler(string bundleId)
    {
        var scheme = CfString("mailto");
        var handler = CfString(bundleId);
        try { return LSSetDefaultHandlerForURLScheme(scheme, handler); }
        finally { CFRelease(handler); CFRelease(scheme); }
    }

    private static unsafe string? DefaultMailtoHandler()
    {
        var scheme = CfString("mailto");
        try
        {
            var handler = LSCopyDefaultHandlerForURLScheme(scheme);
            if (handler == 0) return null;
            try
            {
                var buffer = stackalloc byte[512];
                return CFStringGetCString(handler, buffer, 512, Utf8) != 0 ? Marshal.PtrToStringUTF8((nint)buffer) : null;
            }
            finally { CFRelease(handler); }
        }
        finally { CFRelease(scheme); }
    }

    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private static nint CfString(string value) => CFStringCreateWithCString(0, value, Utf8);
    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)] private static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);
    [LibraryImport(CoreFoundation)] private static unsafe partial byte CFStringGetCString(nint value, byte* buffer, nint size, uint encoding);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(nint value);
    [LibraryImport(CoreServices)] private static partial int LSSetDefaultHandlerForURLScheme(nint scheme, nint handler);
    [LibraryImport(CoreServices)] private static partial nint LSCopyDefaultHandlerForURLScheme(nint scheme);
}
