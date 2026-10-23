using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Runic.Desktop;

// SEC-04: DesktopWebContentPolicy in the WebKitGTK windows (GTK 3 with WebKitGTK 4.1, GTK 4 with WebKitGTK 6.0) under
// Xvfb. A second local listener stands in for an external origin. Real clicks come through XTEST, because WebKit reports
// them as user gestures; scripts run through the Bridge, which WebKit does not. The host's external opener is replaced by
// a recorder, and the test observes downloads on the view's web context or network session, saving them to a
// temporary directory so a failure never writes to the user's download folder.
internal static partial class WebContentSmoke
{
    // Name: gtk3 or gtk4. SetOpener installs the host's test opener, or removes it with null.
    internal sealed record Toolkit(string Name, Action<Action<string>?> SetOpener);

    private static readonly ConcurrentQueue<string> Opened = new();
    private static readonly ConcurrentQueue<string> DownloadsStarted = new();
    private static readonly ConcurrentQueue<string> DownloadsFinished = new();
    private static readonly ConcurrentQueue<string> DownloadsFailed = new();
    private static string s_downloadDirectory = "";
    private static bool s_gtk4;
    private static nint s_webKit;

    internal static async Task RunAsync(DesktopHost host, Toolkit toolkit)
    {
        if (!OperatingSystem.IsLinux()) return;
        s_gtk4 = toolkit.Name == "gtk4";
        s_webKit = NativeLibrary.Load(s_gtk4 ? "libwebkitgtk-6.0.so.4" : "libwebkit2gtk-4.1.so.0");
        if (Pointer.Open() is not { } pointer)
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("XTEST (libXtst.so.6) is required for the web content smoke in CI.");
            throw new InvalidOperationException("XTEST is unavailable; set RUNIC_LIBXTST to a libXtst.so.6 for this architecture.");
        }
        using var _ = pointer;
        using var external = new ExternalOrigin();
        var downloads = Directory.CreateTempSubdirectory("runic-web-content-");
        s_downloadDirectory = downloads.FullName;
        toolkit.SetOpener(Opened.Enqueue);
        try
        {
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Handler((request, _) => ValueTask.FromResult(Serve(request.Path, external.Origin))),
            });
            await DefaultPolicyAsync(surface, toolkit, pointer, external);
            await AllowedDownloadsAsync(surface, toolkit);
            await AllowInWindowAsync(surface, toolkit, pointer, external);
            Console.WriteLine($"Web content ({toolkit.Name}) passed: external clicks open externally, script navigation and window.open " +
                "are cancelled, iframes and same-origin navigation load, downloads follow AllowDownloads, developer extras follow DevTools, " +
                "AllowInWindow loads other origins in the window.");
        }
        finally
        {
            toolkit.SetOpener(null);
            downloads.Delete(recursive: true);
        }
    }

    private static async Task DefaultPolicyAsync(DesktopSurface surface, Toolkit toolkit, Pointer pointer, ExternalOrigin external)
    {
        var development = string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") is { Length: > 0 } dotnet
            ? dotnet : Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
        var policy = DesktopWebContentPolicy.Default;
        await using (var window = await surface.OpenWindowAsync(WindowOptions(toolkit, policy)))
        {
            var page = await surface.ExecuteJavaScriptAsync("return location.href;", TimeSpan.FromSeconds(30));
            var downloadHandler = await ConnectDownloadsAsync(window);
            try
            {
                // (f) The default resolves DevTools for Development only.
                Check(await DeveloperExtrasAsync(window) == development, $"developer extras are not {development} by default");

                // (c) window.open creates no window and, without a gesture, opens nothing. (WebKit's popup blocker refuses it
                // before the host is asked; a clicked target="_blank" link below reaches the host.)
                var opened = await surface.ExecuteJavaScriptAsync($"return String(window.open('{external.Origin}/open'));");
                Check(opened == "null", $"window.open returned {opened}");
                var sameOrigin = await surface.ExecuteJavaScriptAsync("return String(window.open('second'));");
                Check(sameOrigin == "null", $"window.open of the application's origin returned {sameOrigin}");
                await Task.Delay(1000);
                Check(Opened.IsEmpty, "window.open without a gesture was opened externally");
                await CheckStaysAsync(window, page, "window.open");

                // An iframe of another origin is not affected.
                await surface.ExecuteJavaScriptAsync(
                    $"const frame = document.createElement('iframe'); frame.onload = () => window.__frameLoaded = true; frame.src = '{external.Origin}/frame'; document.body.append(frame); return 'ok';");
                await WaitAsync(async () => await surface.ExecuteJavaScriptAsync("return String(window.__frameLoaded === true);") == "true",
                    "the external iframe to load");
                Check(external.Requests.Contains("/frame"), "the external iframe was not requested");
                await CheckStaysAsync(window, page, "an external iframe");

                // (e) <a download> is cancelled once it started.
                await surface.ExecuteJavaScriptAsync("document.getElementById('download').click(); return 'ok';");
                // WebKit reports a failed download as failed, then finished.
                await WaitAsync(() => Task.FromResult(DownloadsFailed.Count == 1), "the <a download> download to be cancelled");
                await Task.Delay(500);
                Check(DownloadsStarted.Count == 1 && Directory.GetFiles(s_downloadDirectory).Length == 0,
                    "the <a download> download saved " + string.Join(", ", Directory.GetFiles(s_downloadDirectory)));
                Console.WriteLine($"Web content ({toolkit.Name}): <a download> cancelled: {DownloadsFailed.Single()}.");

                // (a) A real click on a link to another origin opens it externally and keeps the window on the application.
                // WebKit fires beforeunload before it asks for the navigation decision; the page's Bridge closes only at
                // pagehide, so it stays connected.
                await pointer.ClickAsync(60, 150);
                var clicked = await NextOpenedAsync();
                Check(clicked == external.Origin + "/clicked", $"the clicked link opened {clicked}");
                await CheckStaysAsync(window, page, "a clicked external link");
                await Task.Delay(1000);
                var alive = await surface.ExecuteJavaScriptAsync("return 'connected';");
                Check(alive == "connected", $"the Bridge did not stay connected after a cancelled navigation: {alive}");

                // (c) A real click on a target="_blank" link opens it externally, without a second window.
                await pointer.ClickAsync(200, 150);
                var blank = await NextOpenedAsync();
                Check(blank == external.Origin + "/blank", $"the clicked target=_blank link opened {blank}");
                await CheckStaysAsync(window, page, "a clicked target=_blank link");

                // (c) window.open from a real click (WebKit's create signal) opens it externally, without a second window.
                await pointer.ClickAsync(330, 150);
                var popup = await NextOpenedAsync();
                Check(popup == external.Origin + "/popup", $"the clicked window.open opened {popup}");
                await CheckStaysAsync(window, page, "a clicked window.open");
            }
            finally
            {
                await DisconnectDownloadsAsync(window, downloadHandler);
            }
        }

        // (b) A script navigation to another origin, without a gesture: cancelled, not opened.
        await NavigateAsync(surface, toolkit, policy, $"{external.Origin}/script", async (window, page) =>
        {
            await Task.Delay(1500);
            await CheckStaysAsync(window, page, "a script navigation to another origin");
            Check(external.Requests.Contains("/script"), "the script navigation was not decided by its response");
        });
        // (b) A server that does not answer: no error page replaces the application.
        await NavigateAsync(surface, toolkit, policy, $"http://127.0.0.1:{ExternalOrigin.ClosedPort()}/unreachable", async (window, page) =>
        {
            await Task.Delay(1500);
            await CheckStaysAsync(window, page, "a script navigation to an unreachable server");
        });
        // (e) A document WebKit cannot show is cancelled at its response, before anything downloads.
        await NavigateAsync(surface, toolkit, policy, "file.bin", async (window, page) =>
        {
            await Task.Delay(1500);
            Check(DownloadsStarted.IsEmpty, "a navigation to a binary response started a download");
            await CheckStaysAsync(window, page, "a navigation to a binary response");
        }, observeDownloads: true);
        // (e) An attachment, which WebKit downloads, is cancelled once it started.
        await NavigateAsync(surface, toolkit, policy, "attachment.txt", async (window, page) =>
        {
            await WaitAsync(() => Task.FromResult(DownloadsFailed.Count == 1), "the attachment download to be cancelled");
            await Task.Delay(500);
            Check(DownloadsStarted.Count == 1 && Directory.GetFiles(s_downloadDirectory).Length == 0, "the attachment was saved");
            await CheckStaysAsync(window, page, "a navigation to an attachment");
        }, observeDownloads: true);
        Check(Opened.IsEmpty, "a script navigation was opened externally: " + string.Join(", ", Opened));
        // (d) Navigation within the application's origin loads in the window.
        await NavigateAsync(surface, toolkit, policy, "second", (window, page) =>
            WaitAsync(async () => await ViewUriAsync(window) == new Uri(new Uri(page), "second").AbsoluteUri, "the same-origin navigation to load"));
    }

    // Opens a window with the policy, navigates it from a script and checks the result natively.
    private static async Task NavigateAsync(DesktopSurface surface, Toolkit toolkit, DesktopWebContentPolicy policy, string url,
        Func<DesktopWindow, string, Task> check, bool observeDownloads = false)
    {
        Clear();
        await using var window = await surface.OpenWindowAsync(WindowOptions(toolkit, policy));
        var page = await surface.ExecuteJavaScriptAsync("return location.href;", TimeSpan.FromSeconds(30));
        var downloadHandler = observeDownloads ? await ConnectDownloadsAsync(window) : default;
        try
        {
            await surface.ExecuteJavaScriptAsync($"location = '{url}'; return 'ok';");
            await check(window, page);
        }
        finally
        {
            await DisconnectDownloadsAsync(window, downloadHandler);
        }
    }

    private static async Task AllowedDownloadsAsync(DesktopSurface surface, Toolkit toolkit)
    {
        Clear();
        await using var window = await surface.OpenWindowAsync(WindowOptions(toolkit,
            DesktopWebContentPolicy.Default with { AllowDownloads = true, DevTools = true }));
        await surface.ExecuteJavaScriptAsync("return location.href;", TimeSpan.FromSeconds(30));
        var downloadHandler = await ConnectDownloadsAsync(window);
        try
        {
            Check(await DeveloperExtrasAsync(window), "developer extras are off with DevTools = true");
            await surface.ExecuteJavaScriptAsync("document.getElementById('download').click(); return 'ok';");
            await WaitAsync(() => Task.FromResult(DownloadsFinished.Count == 1), "the allowed <a download> download to finish");
            // WebKit downloads an attachment; a type it cannot show, without one, it ignores whatever the policy.
            await surface.ExecuteJavaScriptAsync("location = 'attachment.txt'; return 'ok';");
            await WaitAsync(() => Task.FromResult(DownloadsFinished.Count == 2), "the allowed attachment to download");
            Check(DownloadsFailed.IsEmpty, "an allowed download failed: " + string.Join(", ", DownloadsFailed));
            var saved = Directory.GetFiles(s_downloadDirectory).Select(File.ReadAllBytes).ToArray();
            Check(saved.Length == 2 && saved.All(content => content.SequenceEqual(FileBytes)), "the allowed downloads saved different content");
            Console.WriteLine($"Web content ({toolkit.Name}): allowed downloads finished; developer extras on with DevTools = true.");
        }
        finally
        {
            await DisconnectDownloadsAsync(window, downloadHandler);
        }
    }

    private static async Task AllowInWindowAsync(DesktopSurface surface, Toolkit toolkit, Pointer pointer, ExternalOrigin external)
    {
        Clear();
        var policy = DesktopWebContentPolicy.Default with { ExternalNavigation = DesktopExternalNavigation.AllowInWindow };
        // (g) A new window for another origin loads it in this window instead. WebKit blocks window.open without a
        // gesture before asking the host, so a clicked target="_blank" link asks.
        await using (var window = await surface.OpenWindowAsync(WindowOptions(toolkit, policy)))
        {
            var opened = await surface.ExecuteJavaScriptAsync($"return String(window.open('{external.Origin}/open'));", TimeSpan.FromSeconds(30));
            Check(opened == "null", $"window.open returned {opened} under AllowInWindow");
            await pointer.ClickAsync(200, 150);
            await WaitAsync(async () => await ViewUriAsync(window) == external.Origin + "/blank", "the new-window link to load in the window");
        }
        // (g) A script navigation to another origin loads in the window.
        await NavigateAsync(surface, toolkit, policy, $"{external.Origin}/script", (window, _) =>
            WaitAsync(async () => await ViewUriAsync(window) == external.Origin + "/script", "the external navigation to load in the window"));
        Check(Opened.IsEmpty, "AllowInWindow opened a URL externally: " + string.Join(", ", Opened));
    }

    private static DesktopWindowOptions WindowOptions(Toolkit toolkit, DesktopWebContentPolicy policy) => new()
    {
        Browser = BrowserKind.Embedded,
        Width = 400,
        Height = 300,
        // GTK 4 cannot place windows; without a window manager they appear at the origin.
        X = toolkit.Name == "gtk3" ? 0 : null,
        Y = toolkit.Name == "gtk3" ? 0 : null,
        WebContent = policy,
    };

    private static readonly byte[] FileBytes = Encoding.ASCII.GetBytes("runic binary\0\x01\x02");

    // The two links and the button each cover a third of the window, for the clicks.
    private static ContentResponse? Serve(string path, string externalOrigin) => path switch
    {
        "/" or "/index.html" => ContentResponse.Text(
            $$"""
            <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic web content</title>
            <style>html,body{margin:0;height:100%} .third{position:fixed;top:0;bottom:0;width:33%;border:0;background:#cde}
            #blank{left:33%;background:#dec} #popup{left:66%;background:#ecd}</style></head>
            <body><a id="external" class="third" href="{{externalOrigin}}/clicked">external link</a>
            <a id="blank" class="third" target="_blank" href="{{externalOrigin}}/blank">new window link</a>
            <button id="popup" class="third" onclick="window.open('{{externalOrigin}}/popup')">window.open</button>
            <a id="download" download="notes.bin" href="file.bin">download</a>
            </body></html>
            """),
        "/second" => ContentResponse.Text("<!doctype html><title>second</title>second"),
        "/file.bin" => new ContentResponse(FileBytes, "application/octet-stream"),
        "/attachment.txt" => new ContentResponse(FileBytes, "text/plain",
            headers: new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=notes.txt" }),
        _ => null,
    };

    // The window still shows the application's page: no navigation and no error page.
    private static async Task CheckStaysAsync(DesktopWindow window, string page, string action)
    {
        var (uri, title) = await ViewAsync(window);
        Check(uri == page && title == "Runic web content", $"after {action} the window shows {uri} ('{title}') instead of {page}");
    }

    private static async Task<string> NextOpenedAsync()
    {
        string? url = null;
        await WaitAsync(() => Task.FromResult(Opened.TryDequeue(out url)), "the external opener");
        Check(Opened.IsEmpty, "more than one URL was opened externally");
        return url!;
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what} (opened [{string.Join(", ", Opened)}], downloads started " +
                    $"[{string.Join(", ", DownloadsStarted)}], {DownloadsFinished.Count} finished, failed [{string.Join(", ", DownloadsFailed)}]).");
            await Task.Delay(100);
        }
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException("Web content: " + failure + ".");
    }

    private static void Clear()
    {
        Opened.Clear();
        DownloadsStarted.Clear();
        DownloadsFinished.Clear();
        DownloadsFailed.Clear();
        foreach (var file in Directory.GetFiles(s_downloadDirectory)) File.Delete(file);
    }

    private static async Task<bool> DeveloperExtrasAsync(DesktopWindow window)
    {
        var enabled = false;
        await window.InvokeNativeAsync(handle => enabled = Native.DeveloperExtras(handle));
        return enabled;
    }

    private static async Task<string?> ViewUriAsync(DesktopWindow window) => (await ViewAsync(window)).Uri;

    private static async Task<(string? Uri, string? Title)> ViewAsync(DesktopWindow window)
    {
        (string?, string?) view = default;
        await window.InvokeNativeAsync(handle => view = (Native.ViewUri(handle), Native.ViewTitle(handle)));
        return view;
    }

    private static async Task<(nint Source, nuint Handler)> ConnectDownloadsAsync(DesktopWindow window)
    {
        (nint, nuint) connection = default;
        await window.InvokeNativeAsync(handle => connection = Native.ConnectDownloads(handle));
        return connection;
    }

    private static async Task DisconnectDownloadsAsync(DesktopWindow window, (nint Source, nuint Handler) connection)
    {
        if (connection.Source == 0 || !window.IsOpen) return;
        await window.InvokeNativeAsync(_ => Native.DisconnectDownloads(connection));
    }

    private static unsafe partial class Native
    {
        internal static bool DeveloperExtras(nint window)
        {
            var settings = ((delegate* unmanaged[Cdecl]<nint, nint>)WebKit("webkit_web_view_get_settings"))(WebView(window));
            return ((delegate* unmanaged[Cdecl]<nint, int>)WebKit("webkit_settings_get_enable_developer_extras"))(settings) != 0;
        }

        internal static string? ViewUri(nint window) =>
            Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<nint, nint>)WebKit("webkit_web_view_get_uri"))(WebView(window)));

        internal static string? ViewTitle(nint window) =>
            Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<nint, nint>)WebKit("webkit_web_view_get_title"))(WebView(window)));

        // download-started on the view's web context (WebKitGTK 4.1) or network session (6.0), after the host's own handler.
        internal static (nint Source, nuint Handler) ConnectDownloads(nint window)
        {
            var source = ((delegate* unmanaged[Cdecl]<nint, nint>)WebKit(s_gtk4 ? "webkit_web_view_get_network_session" : "webkit_web_view_get_context"))(WebView(window));
            ObjectRef(source);
            return (source, ConnectData(source, "download-started",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnDownloadStarted, 0, 0, 0));
        }

        internal static void DisconnectDownloads((nint Source, nuint Handler) connection)
        {
            HandlerDisconnect(connection.Source, connection.Handler);
            ObjectUnref(connection.Source);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnDownloadStarted(nint source, nint download, nint data)
        {
            var request = ((delegate* unmanaged[Cdecl]<nint, nint>)WebKit("webkit_download_get_request"))(download);
            DownloadsStarted.Enqueue(Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<nint, nint>)WebKit("webkit_uri_request_get_uri"))(request)) ?? "");
            ConnectData(download, "decide-destination", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnDecideDestination, 0, 0, 0);
            ConnectData(download, "finished", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnFinished, 0, 0, 0);
            ConnectData(download, "failed", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnFailed, 0, 0, 0);
        }

        // Into the temporary directory: WebKitGTK 4.1 takes a file URI, 6.0 a path.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int OnDecideDestination(nint download, nint suggestedName, nint data)
        {
            var path = Path.Combine(s_downloadDirectory, $"download-{DownloadsStarted.Count}-{Marshal.PtrToStringUTF8(suggestedName)}");
            var destination = Marshal.StringToCoTaskMemUTF8(s_gtk4 ? path : new Uri(path).AbsoluteUri);
            try
            {
                ((delegate* unmanaged[Cdecl]<nint, nint, void>)WebKit("webkit_download_set_destination"))(download, destination);
            }
            finally
            {
                Marshal.FreeCoTaskMem(destination);
            }
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnFinished(nint download, nint data) => DownloadsFinished.Enqueue("finished");

        // GError: domain, code, then the message.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnFailed(nint download, nint error, nint data) => DownloadsFailed.Enqueue(
            error == 0 ? "failed" : Marshal.PtrToStringUTF8(*(nint*)((byte*)error + 8)) ?? "failed");

        // The window's child, or the last child of the box that also holds the menu bar (B10).
        private static nint WebView(nint window)
        {
            var child = s_gtk4 ? Gtk4WindowGetChild(window) : Gtk3BinGetChild(window);
            if (TypeCheckInstanceIsA(child, s_gtk4 ? Gtk4BoxType() : Gtk3BoxType()) == 0) return child;
            if (s_gtk4) return Gtk4WidgetGetLastChild(child);
            var children = Gtk3ContainerGetChildren(child);
            nint last = 0;
            for (var node = children; node != 0; node = ((nint*)node)[1]) last = ((nint*)node)[0];
            GListFree(children);
            return last;
        }

        private static nint WebKit(string name) => NativeLibrary.GetExport(s_webKit, name);

        [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_bin_get_child")]
        private static partial nint Gtk3BinGetChild(nint bin);

        [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_window_get_child")]
        private static partial nint Gtk4WindowGetChild(nint window);

        [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_box_get_type")]
        private static partial nuint Gtk4BoxType();

        [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_get_last_child")]
        private static partial nint Gtk4WidgetGetLastChild(nint widget);

        [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_box_get_type")]
        private static partial nuint Gtk3BoxType();

        [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_container_get_children")]
        private static partial nint Gtk3ContainerGetChildren(nint container);

        [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_list_free")]
        private static partial void GListFree(nint list);

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_type_check_instance_is_a")]
        private static partial int TypeCheckInstanceIsA(nint instance, nuint type);

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nuint ConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_handler_disconnect")]
        private static partial void HandlerDisconnect(nint instance, nuint handler);

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_ref")]
        private static partial nint ObjectRef(nint instance);

        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")]
        private static partial void ObjectUnref(nint instance);
    }

    // Another origin on the loopback interface, recording the paths it served.
    private sealed class ExternalOrigin : IDisposable
    {
        private readonly HttpListener _listener = new();

        internal ExternalOrigin()
        {
            Origin = $"http://127.0.0.1:{ClosedPort()}";
            _listener.Prefixes.Add(Origin + "/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        internal string Origin { get; }

        internal ConcurrentBag<string> Requests { get; } = [];

        // A free port nothing listens on.
        internal static int ClosedPort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        private async Task ServeAsync()
        {
            var body = Encoding.UTF8.GetBytes("<!doctype html><title>external</title>external");
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                Requests.Add(context.Request.Url!.AbsolutePath);
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }
}
