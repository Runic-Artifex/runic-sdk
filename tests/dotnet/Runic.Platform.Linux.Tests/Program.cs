using System.Runtime.InteropServices;
using Microsoft.Testing.Platform.Builder;

namespace Runic.Platform.Linux.Tests;

/// <summary>
/// The entry point is hand-written because the native clipboard case starts this executable again with
/// <c>--observer</c>: an independent process that reads the CLIPBOARD selection through GTK's own API. That
/// invocation runs before the test platform parses arguments.
/// </summary>
internal static class Program
{
#pragma warning disable TUnit0034 // The generated test entry point is disabled; this one also serves the --observer child process.
    public static async Task<int> Main(string[] args)
#pragma warning restore TUnit0034
    {
        if (args is ["--observer"]) return Observe();
        if (args is ["--observer-formats"]) return ObserveFormats();
        if (args is ["--observer-take", var text]) return Take(text);
        ITestApplicationBuilder builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);
        builder.AddSelfRegisteredExtensions(args);
        using ITestApplication application = await builder.BuildAsync().ConfigureAwait(false);
        return await application.RunAsync().ConfigureAwait(false);
    }

    // What a GTK 3 application that pastes sees: each target, then the HTML, the URIs and the image's size, one per line.
    private static int ObserveFormats()
    {
        try
        {
            if (Native.Init(0, 0) == 0) throw new InvalidOperationException("GTK display unavailable.");
            nint clipboard = Native.Clipboard(Native.Atom("CLIPBOARD", 0));
            if (Native.WaitTargets(clipboard, out nint targets, out int count) != 0)
            {
                for (int i = 0; i < count; i++)
                {
                    nint name = Native.AtomName(Marshal.ReadIntPtr(targets, i * IntPtr.Size));
                    Console.WriteLine(Marshal.PtrToStringUTF8(name));
                    Native.Free(name);
                }
                Native.Free(targets);
            }
            Console.WriteLine("html:" + Contents(clipboard, "text/html"));
            Console.WriteLine("uris:" + string.Join(" ", Contents(clipboard, "text/uri-list").Split("\r\n", StringSplitOptions.RemoveEmptyEntries)));
            nint image = Native.WaitImage(clipboard);
            Console.WriteLine(image == 0 ? "image:none" : $"image:{Native.PixbufWidth(image)}x{Native.PixbufHeight(image)}");
            if (image != 0) Native.Unref(image);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        static string Contents(nint clipboard, string target)
        {
            nint selection = Native.WaitContents(clipboard, Native.Atom(target, 0));
            if (selection == 0) return "";
            try { return Native.DataLength(selection) <= 0 ? "" : Marshal.PtrToStringUTF8(Native.Data(selection), Native.DataLength(selection)); }
            finally { Native.DataFree(selection); }
        }
    }

    // Takes the clipboard with text for a moment, as another application's copy does, then exits.
    private static int Take(string text)
    {
        try
        {
            if (Native.Init(0, 0) == 0) throw new InvalidOperationException("GTK display unavailable.");
            Native.SetText(Native.Clipboard(Native.Atom("CLIPBOARD", 0)), text, -1);
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < until) { while (Native.Iteration(0, 0) != 0) { } Thread.Sleep(10); }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static int Observe()
    {
        try
        {
            if (Native.Init(0, 0) == 0) throw new InvalidOperationException("GTK display unavailable.");
            nint text = Native.WaitText(Native.Clipboard(Native.Atom("CLIPBOARD", 0)));
            try { Console.Write(Marshal.PtrToStringUTF8(text) ?? "<no-text>"); }
            finally { if (text != 0) Native.Free(text); }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
