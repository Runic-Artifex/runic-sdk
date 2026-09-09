using System.Diagnostics;
using System.Runtime.InteropServices;
using Runic.Platform;
using Runic.Platform.Runtime;
using Runic.Platform.Windows;

if (args.Length > 0 && args[0] is "--native-notifications" or "--notification-activation")
    return await NotificationTests.RunAsync(args);
if (args.Length > 0) return NativeTests.Run(args);
Check(WindowsDesktopNotifications.NotificationSettingResult(unchecked((int)0x80070490), 0) is PlatformResult<Unit>.Success);
Check(WindowsDesktopNotifications.NotificationSettingResult(0, 0) is PlatformResult<Unit>.Success);
foreach (var setting in new[] { 1, 2, 3, 4, 99 })
    Check(WindowsDesktopNotifications.NotificationSettingResult(0, setting) is PlatformResult<Unit>.Failed { Code: FailureCode.PermissionDenied });
Check(WindowsDesktopNotifications.NotificationSettingResult(unchecked((int)0x80070005), 0) is PlatformResult<Unit>.Failed { Code: FailureCode.PermissionDenied });
Check(WindowsDesktopNotifications.NotificationSettingResult(unchecked((int)0x80004005), 0) is PlatformResult<Unit>.Unavailable);
Console.WriteLine("PASS notification first-use eligibility, denial and backend failure classification");
var xml = System.Xml.Linq.XElement.Parse(WindowsDesktopNotifications.ToastXml(new("saved", "<Title>", "A & B")
{ Actions = [new("open", "Open <result>")], ActivationUri = new Uri("runic-test://result/1") }));
Check(xml.Descendants("text").First().Value == "<Title>");
Check(xml.Descendants("action").Single().Attribute("activationType")?.Value == "protocol");
Check(xml.Descendants("action").Single().Attribute("arguments")?.Value.Contains("runic-action=open") == true);
BufferTests.Run();
var owner = new Owner();
var native = new FakeClipboard();
var clipboard = new WindowsTextClipboard(owner, native);
Check(await clipboard.ReadTextAsync(0) is PlatformResult<string?>.Success { Value: null });
native.Text = "";
Check(await clipboard.ReadTextAsync(0) is PlatformResult<string?>.Success { Value: "" });
native.Text = "long";
Check(await clipboard.ReadTextAsync(2) is PlatformResult<string?>.Failed { Code: FailureCode.TooLarge });
using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try { await clipboard.WriteTextAsync("ignored", cancellation.Token); throw new InvalidOperationException("Cancellation ignored"); }
catch (OperationCanceledException) { }
Check(native.Writes == 0);
using var late = new CancellationTokenSource();
native.OnWrite = late.Cancel;
Check(await clipboard.WriteTextAsync("committed", late.Token) is PlatformResult<Unit>.Success);
Check(native.Text == "committed" && native.Writes == 1);
Check(await clipboard.WriteTextAsync("invalid\0text") is PlatformResult<Unit>.Failed { Code: FailureCode.InvalidData });
Check(native.Writes == 1);
foreach (var code in new[] { FailureCode.PermissionDenied, FailureCode.ResourceBusy, FailureCode.IoError })
{
    native.Failure = code;
    Check(await clipboard.ReadTextAsync(10) is PlatformResult<string?>.Failed failed && failed.Code == code);
}
native.Failure = null;
owner.BeforeInvoke = () => owner.Generation = Guid.NewGuid();
Check(await clipboard.WriteTextAsync("stale") is PlatformResult<Unit>.Unavailable);
Check(native.Writes == 1);
owner.BeforeInvoke = null;
Check(await clipboard.WriteTextAsync("retry") is PlatformResult<Unit>.Success);
owner.IsAvailable = false;
Check(await clipboard.ReadTextAsync(10) is PlatformResult<string?>.Unavailable);
Console.WriteLine("PASS Windows clipboard portable cancellation, bounds, outcomes, generation, retry conformance");
return 0;

static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Clipboard conformance assertion failed"); }

internal sealed class Owner : INativePickerOwner
{
    public Guid Generation { get; set; } = Guid.NewGuid();
    public bool IsAvailable { get; set; } = true;
    internal Action? BeforeInvoke;
    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeInvoke?.Invoke();
        action(1);
        return ValueTask.CompletedTask;
    }
}
internal sealed class FakeClipboard : IWindowsClipboard
{
    internal string? Text;
    internal int Writes;
    internal Action? OnWrite;
    internal FailureCode? Failure;
    public PlatformResult<string?> Read(nint owner, int maximumCharacters) => Failure is { } failure
        ? new PlatformResult<string?>.Failed(failure)
        : Text?.Length > maximumCharacters ? new PlatformResult<string?>.Failed(FailureCode.TooLarge)
        : new PlatformResult<string?>.Success(Text);
    public PlatformResult<Unit> Write(nint owner, string text)
    { Writes++; Text = text; OnWrite?.Invoke(); return new PlatformResult<Unit>.Success(new()); }
}

internal static partial class NativeTests
{
    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native clipboard evidence requires Windows.");
        if (args[0] == "--native-hold")
        {
            if (Win32Clipboard.OpenClipboard(0) == 0) throw new InvalidOperationException("Could not hold clipboard");
            try { Console.WriteLine("ready"); Console.Out.Flush(); Console.ReadLine(); }
            finally { Win32Clipboard.CloseClipboard(); }
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
                Assert(native.Write(window, args[1]) is PlatformResult<Unit>.Success);
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
                if (text.Length > 0) Assert(native.Read(window, text.Length - 1) is PlatformResult<string?>.Failed { Code: FailureCode.TooLarge });
            }
            Assert(Win32Clipboard.OpenClipboard(window) != 0);
            try { Assert(Win32Clipboard.EmptyClipboard() != 0); }
            finally { Win32Clipboard.CloseClipboard(); }
            Assert(native.Read(window, 0) is PlatformResult<string?>.Success { Value: null });
            using (var holder = Start("--native-hold"))
            {
                try
                {
                    Assert(holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult() == "ready");
                    Assert(native.Read(window, 10) is PlatformResult<string?>.Failed { Code: FailureCode.ResourceBusy or FailureCode.PermissionDenied });
                    Assert(native.Write(window, "busy") is PlatformResult<Unit>.Failed { Code: FailureCode.ResourceBusy or FailureCode.PermissionDenied });
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
            Assert(native.Write(window, "post-busy retry") is PlatformResult<Unit>.Success);
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

internal static class BufferTests
{
    internal static unsafe void Run()
    {
        char* empty = stackalloc char[] { '\0' };
        char* valid = stackalloc char[] { 'a', 'b', '\0' };
        char* malformed = stackalloc char[] { 'a', 'b' };
        Assert(Win32Clipboard.DecodeText(empty, 1, 0) is PlatformResult<string?>.Success { Value: "" });
        Assert(Win32Clipboard.DecodeText(valid, 3, 2) is PlatformResult<string?>.Success { Value: "ab" });
        Assert(Win32Clipboard.DecodeText(valid, 3, 1) is PlatformResult<string?>.Failed { Code: FailureCode.TooLarge });
        Assert(Win32Clipboard.DecodeText(malformed, 2, 2) is PlatformResult<string?>.Failed { Code: FailureCode.InvalidData });
        Assert(Win32Clipboard.DecodeText(malformed, 2, int.MaxValue) is PlatformResult<string?>.Failed { Code: FailureCode.InvalidData });
        Assert(Win32Clipboard.DecodeText(null, 0, 0) is PlatformResult<string?>.Failed { Code: FailureCode.InvalidData });
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Native text buffer bound assertion failed"); }
}
