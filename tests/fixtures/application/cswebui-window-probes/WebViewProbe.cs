using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

namespace CsWebUiWindowProbes;

// A real CS-WebUI WebView window: WebView2 on Windows, WebKitGTK on Linux (which needs a display, such as xvfb-run with
// GDK_BACKEND=x11). Not part of the package smoke, which builds Program.cs alone. One window per process: with WebUI at
// 24c7d196, a second webui_wait() in a Linux WebView process does not return after its window closed (cs-webui's tests).
internal static partial class WebViewProbe
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    // sdk#1148, --probe-webview-close-guard: the user's close of a guarded Window stays open while CanCloseAsync refuses,
    // RequestCloseAsync asks the same guard, and an allowed user close closes the window through WebUiWindow.Close.
    internal static int RunCloseGuard()
    {
        if (!Prepare(out var root)) return 0;
        var log = new RecordingLoggerFactory();
        var services = new ServiceCollection();
        services.AddScoped<GuardedProbeViewModel>();
        services.AddRunicViews();
        services.AddSingleton<ILoggerFactory>(log);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var result = RunicCsWebUiHost.Run(provider, new RunicCsWebUiHostOptions { UseWebView = true }, async host =>
        {
            var window = await host.OpenWindowAsync<GuardedProbeWindow>(new RunicWindowOptions { ContentRoot = root, Width = 640, Height = 480 });
            var native = CsWebUiPlatformServices.GetWebUiWindow(window) ?? throw new InvalidOperationException("The Window has no WebUI window.");
            window.Answer = false;
            RequestUserClose(native);
            await Until(() => window.Asked == 1, "The user's close did not ask CanCloseAsync.");
            await Task.Delay(500);
            Check(native.IsShown && window.Asked == 1, $"A refused user close did not keep the window open (shown {native.IsShown}, asked {window.Asked}).");

            Check(!await window.RequestCloseAsync().AsTask().WaitAsync(Limit) && window.Asked == 2 && native.IsShown,
                "RequestCloseAsync did not keep the window open when CanCloseAsync refused.");

            window.Answer = true;
            RequestUserClose(native);
            await window.WaitForCloseAsync().AsTask().WaitAsync(Limit);
            Check(window.Asked == 3, $"An allowed user close asked CanCloseAsync {window.Asked - 2} times.");
            await window.DisposeAsync();
            return 7;
        });
        Check(result == 7, $"Run returned {result} instead of the application's result.");
        lock (log.Entries)
            Check(!log.Entries.Any(static entry => entry.EventId.Id == 1053),
                "A guarded WebView Window logged event 1053:\n" + string.Join('\n', log.Entries.Select(static entry => entry.ToString())));
        Console.WriteLine("CSWEBUI_WEBVIEW_CLOSE_GUARD_OK");
        return 0;
    }

    // The bundled WebUI, a page that loads webui.js so WebUI sees the window connect, and a watchdog. False on macOS,
    // where WebUI's WebView cannot keep a window open and Runic's CI does not show WKWebView windows.
    private static bool Prepare(out string root)
    {
        root = "";
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            Console.WriteLine("CSWEBUI_WEBVIEW_SKIPPED: Windows and Linux only");
            return false;
        }
        Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", null);
        new Thread(static () =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(2));
            Console.Error.WriteLine("The WebView probe did not finish within 2 minutes.");
            Environment.Exit(3);
        })
        { IsBackground = true }.Start();
        root = Directory.CreateTempSubdirectory("runic-cswebui-webview-").FullName;
        File.WriteAllText(Path.Combine(root, "index.html"),
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Probe</title><script src=\"/webui.js\"></script></head><body>Probe</body></html>");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        };
        Root = root;
        return true;
    }

    private static string Root { get; set; } = "";

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            Check(DateTime.UtcNow < deadline, message);
            await Task.Delay(20);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Closes the window as its close button does: WM_CLOSE on Windows, GTK's delete-event (gtk_window_close, on GTK's main
    // loop) on Linux.
    private static unsafe void RequestUserClose(WebUiWindow window)
    {
        var handle = window.NativeHandle;
        Check(handle != 0, "WebUI reported no native window.");
        if (OperatingSystem.IsWindows())
        {
            Check(PostMessageW(handle, 0x0010, 0, 0) != 0, "WM_CLOSE could not be posted.");
            return;
        }
        var gtk = NativeLibrary.Load("libgtk-3.so.0");
        s_gtkWindowClose = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(gtk, "gtk_window_close");
        var idleAdd = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<nint, int>, nint, uint>)NativeLibrary.GetExport(gtk, "g_idle_add");
        idleAdd(&CloseOnMainLoop, handle);
    }

    private static unsafe delegate* unmanaged[Cdecl]<nint, void> s_gtkWindowClose;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int CloseOnMainLoop(nint gtkWindow)
    {
        s_gtkWindowClose(gtkWindow);
        return 0;
    }

    [LibraryImport("user32.dll")]
    private static partial int PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}
