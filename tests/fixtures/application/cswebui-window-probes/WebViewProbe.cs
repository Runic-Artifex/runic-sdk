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

    // sdk#1149, --probe-webview-placement: RunicCsWebUiHostOptions.Placement places the shown window; the presentation
    // reports the placement, notices a move it did not make (polling), maximizes and restores over the same restored bounds,
    // and keeps the placement the window had when the user closed it. On Linux under a window manager (Openbox in CI),
    // which maximizes; GDK_BACKEND=x11 gives a position.
    internal static int RunPlacement()
    {
        if (!Prepare(out var root)) return 0;
        var services = new ServiceCollection();
        services.AddScoped<ProbeViewModel>();
        services.AddRunicViews();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var initial = new RunicWindowPlacement { X = 150, Y = 120, Width = 700, Height = 500 };
        RunicWindowPlacement? closedAt = null;
        var options = new RunicCsWebUiHostOptions
        {
            UseWebView = true,
            Placement = window => window is ProbeWindow ? initial : null,
        };
        var result = RunicCsWebUiHost.Run(provider, options, async host =>
        {
            var window = await host.OpenWindowAsync<ProbeWindow>(new RunicWindowOptions { ContentRoot = root, Width = 400, Height = 300 });
            var native = CsWebUiPlatformServices.GetWebUiWindow(window) ?? throw new InvalidOperationException("The Window has no WebUI window.");
            var presentation = window.Presentation;
            Check(presentation.Features.HasFlag(RunicWindowPresentationFeatures.Placement | RunicWindowPresentationFeatures.Maximize),
                $"A WebView window reported {presentation.Features}.");
            var changes = 0;
            presentation.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(IRunicWindowPresentation.Placement)) Interlocked.Increment(ref changes);
            };
            await Until(() => Same(presentation.Placement, initial), $"The initial placement was not taken: {presentation.Placement}.");

            // A move the presentation did not make, as the user's drag: the poll reports it.
            var before = Volatile.Read(ref changes);
            Check(native.TrySetPlacement(new WebUiWindowPlacement(260, 140, 720, 520)), "TrySetPlacement failed.");
            await Until(() => Same(presentation.Placement, new RunicWindowPlacement { X = 260, Y = 140, Width = 720, Height = 520 })
                && Volatile.Read(ref changes) > before, $"The poll did not report the move: {presentation.Placement}.");

            var placed = new RunicWindowPlacement { X = 200, Y = 100, Width = 640, Height = 480 };
            Check(await presentation.SetPlacementAsync(placed) is Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Success,
                "SetPlacementAsync failed.");
            await Until(() => Same(presentation.Placement, placed), $"SetPlacementAsync was not reported: {presentation.Placement}.");
            Check(await presentation.MaximizeAsync() is Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Success, "MaximizeAsync failed.");
            await Until(() => presentation.Placement is { Maximized: true } maximized && Same(maximized with { Maximized = false }, placed),
                $"MaximizeAsync did not keep the restored bounds: {presentation.Placement}.");
            Check(await presentation.RestoreAsync() is Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Success, "RestoreAsync failed.");
            await Until(() => Same(presentation.Placement, placed), $"RestoreAsync did not restore: {presentation.Placement}.");

            RequestUserClose(native);
            await window.WaitForCloseAsync().AsTask().WaitAsync(Limit);
            closedAt = presentation.Placement;
            await window.DisposeAsync();
            return 7;
        });
        Check(result == 7, $"Run returned {result} instead of the application's result.");
        Check(Same(closedAt, new RunicWindowPlacement { X = 200, Y = 100, Width = 640, Height = 480 }),
            $"The placement at the user's close was {closedAt}.");
        Console.WriteLine("CSWEBUI_WEBVIEW_PLACEMENT_OK");
        return 0;
    }

    // sdk#1190, --probe-webview-close-drain: with a close drain, the user's close keeps the window open while the Window's
    // accepted work runs, a second close is ignored, and the window closes once the work has ended.
    internal static int RunCloseDrain()
    {
        if (!Prepare(out var root)) return 0;
        var log = new RecordingLoggerFactory();
        var services = new ServiceCollection();
        services.AddScoped<ProbeViewModel>();
        services.AddRunicViews();
        services.AddRunicWindowService();
        services.AddSingleton<ILoggerFactory>(log);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var options = new RunicCsWebUiHostOptions { UseWebView = true, CloseDrain = RunicCloseDrain.For(TimeSpan.FromSeconds(60)) };
        var result = RunicCsWebUiHost.Run(provider, options, async host =>
        {
            var window = await host.OpenWindowAsync<ProbeWindow>(new RunicWindowOptions { ContentRoot = root, Width = 640, Height = 480 });
            var native = CsWebUiPlatformServices.GetWebUiWindow(window) ?? throw new InvalidOperationException("The Window has no WebUI window.");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var work = new AcceptedWorkScope();
            _ = work.RunAsync(() => release.Task);
            using var drained = window.Services.GetRequiredService<IRunicWindowContext>().DrainOnClose(work);
            var windows = window.Services.GetRequiredService<IRunicWindowService>();
            var closed = window.WaitForCloseAsync().AsTask();

            RequestUserClose(native);
            await Until(() => !windows.Windows.Contains(window), "The decided close did not take the Window out of IRunicWindowService.Windows.");
            await Task.Delay(1000);
            Check(native.IsShown && !closed.IsCompleted, "The window closed while its work was draining.");
            RequestUserClose(native);
            await Task.Delay(500);
            Check(native.IsShown && !closed.IsCompleted, "A second user close during the drain closed the window.");

            release.SetResult();
            await closed.WaitAsync(Limit);
            // WebUI closes the window on its own thread; a drained Window has released it.
            await Until(() => !Shown(native), "The window stayed open after its work had drained.");
            await window.DisposeAsync();
            return 7;
        });
        Check(result == 7, $"Run returned {result} instead of the application's result.");
        lock (log.Entries)
            Check(!log.Entries.Any(static entry => entry.EventId.Id == 1055),
                "A WebView Window with a close drain logged event 1055:\n" + string.Join('\n', log.Entries.Select(static entry => entry.ToString())));
        Console.WriteLine("CSWEBUI_WEBVIEW_CLOSE_DRAIN_OK");
        return 0;
    }

    // The same bounds and state; Wayland reports no position, so there only the size and state count.
    private static bool Same(RunicWindowPlacement? actual, RunicWindowPlacement expected) =>
        actual is not null && actual.Width == expected.Width && actual.Height == expected.Height && actual.Maximized == expected.Maximized
        && (actual.X is null || (actual.X == expected.X && actual.Y == expected.Y));

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

    private static bool Shown(WebUiWindow window)
    {
        try { return window.IsShown; }
        catch (ObjectDisposedException) { return false; }
    }

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
