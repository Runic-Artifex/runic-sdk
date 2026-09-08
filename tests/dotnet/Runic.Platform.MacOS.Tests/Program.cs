using System.Diagnostics;
using System.Runtime.InteropServices;
using Runic.Platform;
using Runic.Platform.MacOS;
using Runic.Platform.Runtime;

if (!OperatingSystem.IsMacOS())
{
    try { MacOSPlatformProvider.CreateTextClipboard(new Owner()); throw new InvalidOperationException("Missing OS guard."); }
    catch (PlatformNotSupportedException) { Console.WriteLine("PASS platform guard (native checks require macOS)"); }
    return;
}
NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
var owner = new Owner();
var clipboard = MacOSPlatformProvider.CreateTextClipboard(owner);
PasteboardProbe.Clear();
Check(clipboard.ReadTextAsync(0).AsTask().GetAwaiter().GetResult() is PlatformResult<string?>.Success { Value: null }, "Absent text became empty text.");
foreach (string value in new[] { "", "Runic clipboard é 日本語 🜁", "embedded\0nul" })
{
    Check(clipboard.WriteTextAsync(value).AsTask().GetAwaiter().GetResult() is PlatformResult<Unit>.Success, "Native write failed.");
    var read = clipboard.ReadTextAsync(value.Length).AsTask().GetAwaiter().GetResult();
    Check(read is PlatformResult<string?>.Success success && success.Value == value, "UTF-16 round trip or empty text failed.");
    if (value.Length != 0) Check(clipboard.ReadTextAsync(value.Length - 1).AsTask().GetAwaiter().GetResult() is PlatformResult<string?>.Failed { Code: FailureCode.TooLarge }, "Read bound ignored.");
}
foreach (bool bigEndian in new[] { false, true })
{
    var encoding = new System.Text.UnicodeEncoding(bigEndian, true, true);
    byte[] encoded = [.. encoding.GetPreamble(), .. encoding.GetBytes("日本語 🜁")];
    PasteboardProbe.SetSecondItem("public.utf16-plain-text", encoded);
    Check(clipboard.ReadTextAsync(6).AsTask().GetAwaiter().GetResult() is PlatformResult<string?>.Success { Value: "日本語 🜁" }, "Second-item UTF16/BOM read failed.");
    Check(clipboard.ReadTextAsync(5).AsTask().GetAwaiter().GetResult() is PlatformResult<string?>.Failed { Code: FailureCode.TooLarge }, "UTF16 bound ignored.");
}
PasteboardProbe.SetSecondItem("public.utf16-plain-text", [0x41]);
Check(clipboard.ReadTextAsync(100).AsTask().GetAwaiter().GetResult() is PlatformResult<string?>.Failed { Code: FailureCode.InvalidData }, "Malformed UTF16 accepted.");
const string observation = "Runic independent clipboard observation é 日本語";
Check(clipboard.WriteTextAsync(observation).AsTask().GetAwaiter().GetResult() is PlatformResult<Unit>.Success, "Observer write failed.");
Check(ProcessText("/usr/bin/pbpaste", null) == observation, "Independent pbpaste did not observe native write.");
ProcessText("/usr/bin/pbcopy", observation + " external");
Check(clipboard.ReadTextAsync(1000).AsTask().GetAwaiter().GetResult() is PlatformResult<string?>.Success { Value: observation + " external" }, "External pbcopy was not observed.");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try { clipboard.WriteTextAsync("must not write", cancelled.Token).AsTask().GetAwaiter().GetResult(); throw new InvalidOperationException("Cancellation ignored."); }
catch (OperationCanceledException) { }
Check(clipboard.WriteTextAsync(observation).AsTask().GetAwaiter().GetResult() is PlatformResult<Unit>.Success, "Post cancellation retry failed.");
owner.Available = false;
Check(clipboard.ReadTextAsync(100).AsTask().GetAwaiter().GetResult() is PlatformResult<string?>.Unavailable, "Closed owner accepted.");
Console.WriteLine("PASS native AppKit clipboard with independent pbcopy/pbpaste observation");

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static string ProcessText(string file, string? input)
{
    using var process = Process.Start(new ProcessStartInfo(file) { RedirectStandardInput = input is not null, RedirectStandardOutput = true })!;
    if (input is not null) { process.StandardInput.Write(input); process.StandardInput.Close(); }
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    if (!process.WaitForExit(15000))
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
        throw new TimeoutException("External clipboard observer did not exit within 15 seconds.");
    }
    string output = outputTask.GetAwaiter().GetResult();
    Check(process.ExitCode == 0, "External clipboard observer failed.");
    return output;
}
sealed class Owner : INativePickerOwner
{
    public Guid Generation { get; } = Guid.NewGuid();
    public bool Available { get; set; } = true;
    public bool IsAvailable => Available;
    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Available) throw new InvalidOperationException("Closed owner.");
        action(0);
        return ValueTask.CompletedTask;
    }
}

static partial class PasteboardProbe
{
    internal static void Clear() => Send(Send(Class("NSPasteboard"), Selector("generalPasteboard")), Selector("clearContents"));
    internal static unsafe void SetSecondItem(string flavorName, byte[] bytes)
    {
        nint name = CFStringCreateWithCString(0, "com.apple.pasteboard.clipboard", 0x08000100);
        nint image = CFStringCreateWithCString(0, "public.png", 0x08000100);
        nint flavor = CFStringCreateWithCString(0, flavorName, 0x08000100);
        nint board = 0, data = 0;
        try
        {
            if (PasteboardCreate(name, out board) != 0 || PasteboardClear(board) != 0) throw new InvalidOperationException("Probe pasteboard setup failed.");
            fixed (byte* pointer = bytes) data = CFDataCreate(0, pointer, bytes.Length);
            if (PasteboardPutItemFlavor(board, 1, image, data, 0) != 0 || PasteboardPutItemFlavor(board, 2, flavor, data, 0) != 0)
                throw new InvalidOperationException("Probe multi-item write failed.");
        }
        finally
        {
            if (data != 0) CFRelease(data);
            if (board != 0) CFRelease(board);
            CFRelease(name); CFRelease(image); CFRelease(flavor);
        }
    }
    private const string Services = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [LibraryImport(Services)] private static partial int PasteboardCreate(nint name, out nint board);
    [LibraryImport(Services)] private static partial int PasteboardClear(nint board);
    [LibraryImport(Services)] private static partial int PasteboardPutItemFlavor(nint board, nint item, nint flavor, nint data, uint flags);
    [LibraryImport(Foundation)] private static partial void CFRelease(nint value);
    [LibraryImport(Foundation, StringMarshalling = StringMarshalling.Utf8)] private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);
    [LibraryImport(Foundation)] private static unsafe partial nint CFDataCreate(nint allocator, byte* bytes, nint length);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Selector(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial nint Send(nint receiver, nint selector);
}
