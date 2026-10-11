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
        ITestApplicationBuilder builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);
        builder.AddSelfRegisteredExtensions(args);
        using ITestApplication application = await builder.BuildAsync().ConfigureAwait(false);
        return await application.RunAsync().ConfigureAwait(false);
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
