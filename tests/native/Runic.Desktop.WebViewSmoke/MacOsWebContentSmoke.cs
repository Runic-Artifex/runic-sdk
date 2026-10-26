using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Desktop;
using Runic.Desktop.Internal;

// SEC-04 in the macOS WKWebView window on a real AppKit event loop: the web view's navigation and UI delegate apply
// DesktopWebContentPolicy. The page's scripts run without a user gesture, so they check the paths a page can take on
// its own: an external navigation is cancelled without opening anything, window.open returns null and a same-origin
// navigation loads. A click cannot be synthesized for WebKit's navigation type, so the gesture paths send the
// delegate's own selectors with stand-in WKNavigationAction, WKFrameInfo and WKNavigationResponse objects and a decision
// handler block, as WebKit would after a click. The external opener is replaced to observe what would open in the
// browser. Downloads (G-9) run for real: <a download>, a blob export and a Content-Disposition attachment are saved, under
// names that never replace a file, to a temporary folder in place of ~/Downloads when AllowDownloads is set, and nothing
// is saved by default. Run with --web-content on macOS.
internal static partial class MacOsWebContentSmoke
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentQueue<string> Opened = new();
    private const nint LinkActivated = 0, Other = -1;

    internal static void Run(DesktopHostOptions hostOptions) => DesktopEventLoop.Run(async () =>
    {
        var original = MacOsWkWebViewHost.ExternalOpener;
        MacOsWkWebViewHost.ExternalOpener = Opened.Enqueue;
        try
        {
            await using var host = await DesktopHost.StartAsync(hostOptions);
            await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
            await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded,
                Width = 640,
                Height = 480,
                WebContent = DesktopWebContentPolicy.Default with { DevTools = true },
            }))
            {
                try { await ExerciseOpenInBrowserAsync(surface, window); }
                finally { if (window.IsOpen) await window.CloseAsync(); }
            }
            await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) }))
            await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded,
                Width = 640,
                Height = 480,
                WebContent = new DesktopWebContentPolicy { ExternalNavigation = DesktopExternalNavigation.Block, DevTools = false },
            }))
            {
                try { await ExerciseBlockAsync(window); }
                finally { if (window.IsOpen) await window.CloseAsync(); }
            }
            await ExerciseDownloadsAsync(host);
            Console.WriteLine("WEB CONTENT DONE");
        }
        finally
        {
            MacOsWkWebViewHost.ExternalOpener = original;
        }
    });

    private static async Task ExerciseOpenInBrowserAsync(DesktopSurface surface, DesktopWindow window)
    {
        var page = await surface.ExecuteJavaScriptAsync("return location.href;", StepTimeout);
        var document = await surface.ExecuteJavaScriptAsync("return window.documentId;", StepTimeout);

        var delegates = await NativeAsync(window, static webView => Send(webView, Sel("navigationDelegate")) != 0
            && Send(webView, Sel("navigationDelegate")) == Send(webView, Sel("UIDelegate")) ? null : "the web view has no navigation and UI delegate");
        Expect(delegates);
        Expect(await NativeAsync(window, static webView => Inspectable(webView) is false ? "isInspectable is NO although DevTools is enabled" : null));
        Pass("the web view has one navigation and UI delegate, and isInspectable is YES with DevTools on macOS 13.3 and later.");

        // A script without a user gesture cannot leave the window, nor have the browser open the URL.
        await surface.ExecuteJavaScriptAsync("location.href = 'https://example.invalid/runic-script'; return true;", StepTimeout);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var after = await surface.ExecuteJavaScriptAsync("return location.href + ' ' + window.documentId;", StepTimeout);
        if (after != page + " " + document) throw new InvalidOperationException($"A script navigated the window away: {after}.");
        ExpectNothingOpened("a script navigation");
        Pass("a script navigation to another origin is cancelled and nothing opens.");

        var popup = await surface.ExecuteJavaScriptAsync("return String(window.open('https://example.invalid/runic-popup') === null);", StepTimeout);
        if (popup != "true") throw new InvalidOperationException($"window.open without a gesture returned a window ({popup}).");
        await Task.Delay(TimeSpan.FromSeconds(0.5));
        ExpectNothingOpened("window.open");
        Pass("window.open without a gesture returns null and nothing opens.");

        // The gesture paths, as WebKit sends them after a click.
        Expect(await NativeAsync(window, webView =>
            Decide(webView, "https://example.org/runic-link", MainFrame.Main, LinkActivated, expected: 0)
            ?? Decide(webView, "https://example.org/runic-blank", MainFrame.None, LinkActivated, expected: 0)
            ?? Decide(webView, "https://example.org/runic-other", MainFrame.Main, Other, expected: 0)
            ?? Decide(webView, page + "?link", MainFrame.Main, LinkActivated, expected: 1)
            ?? Decide(webView, "https://example.org/runic-frame", MainFrame.Sub, Other, expected: 1)
            ?? Decide(webView, page + "file.zip", MainFrame.Main, LinkActivated, expected: 0, download: true)
            ?? CreateWebView(webView, "https://example.org/runic-window-open")
            ?? Respond(webView, canShow: false, expected: 0)
            ?? Respond(webView, canShow: true, expected: 1)));
        var opened = Opened.ToArray();
        Opened.Clear();
        string[] expectedOpened = ["https://example.org/runic-link", "https://example.org/runic-blank", "https://example.org/runic-window-open"];
        if (!opened.SequenceEqual(expectedOpened))
            throw new InvalidOperationException($"Expected the browser to open {string.Join(", ", expectedOpened)}; it opened {string.Join(", ", opened)}.");
        Pass("a clicked external link, a target=_blank link and window.open from a gesture open in the browser; the window keeps its origin, frames and showable responses.");

        // A navigation within the application's origin loads.
        await surface.ExecuteJavaScriptAsync("location.href = location.pathname + '?same-origin'; return true;", StepTimeout);
        var deadline = DateTime.UtcNow + StepTimeout * 2;
        string? state = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                state = await surface.ExecuteJavaScriptAsync("return location.search + ' ' + document.readyState + ' ' + window.documentId;", TimeSpan.FromSeconds(2));
                if (state.StartsWith("?same-origin complete ", StringComparison.Ordinal) && !state.EndsWith(" " + document, StringComparison.Ordinal)) break;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The bridge reconnects with the new document.
                state = error.Message;
            }
            await Task.Delay(100);
        }
        if (state is null || !state.StartsWith("?same-origin complete ", StringComparison.Ordinal))
            throw new TimeoutException($"A same-origin navigation did not load: {state}.");
        ExpectNothingOpened("a same-origin navigation");
        Pass("a same-origin navigation loads in the window.");
    }

    private static async Task ExerciseBlockAsync(DesktopWindow window)
    {
        Expect(await NativeAsync(window, static webView => Inspectable(webView) is true ? "isInspectable is YES although DevTools is disabled" : null));
        Expect(await NativeAsync(window, static webView =>
            Decide(webView, "https://example.org/runic-blocked", MainFrame.Main, LinkActivated, expected: 0)
            ?? Decide(webView, "https://example.org/runic-blocked-blank", MainFrame.None, LinkActivated, expected: 0)
            ?? CreateWebView(webView, "https://example.org/runic-blocked-popup")));
        ExpectNothingOpened("Block");
        Pass("Block cancels clicked external links and window.open without opening them; isInspectable follows DevTools.");
    }

    private static readonly byte[] FileBytes = "runic binary\0\x01\x02"u8.ToArray();

    // The page and the files it downloads.
    private static ContentResponse? Serve(string path) => path switch
    {
        "/" or "/index.html" => ContentResponse.Text(DownloadPage),
        "/file.bin" => new ContentResponse(FileBytes, "application/octet-stream"),
        "/attachment.txt" => new ContentResponse(FileBytes, "text/plain",
            headers: new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=notes.txt" }),
        _ => null,
    };

    private static async Task ExerciseDownloadsAsync(DesktopHost host)
    {
        var originalDirectory = MacOsWkWebViewHost.DownloadDirectory;
        var directory = Directory.CreateTempSubdirectory("runic-downloads-");
        MacOsWkWebViewHost.DownloadDirectory = () => directory.FullName;
        try
        {
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Handler((request, _) => ValueTask.FromResult(Serve(request.Path))),
            });
            // Default policy: nothing is saved, and the page stays.
            await using (var window = await surface.OpenWindowAsync(DownloadWindow(DesktopWebContentPolicy.Default with { DevTools = false })))
            {
                try
                {
                    var page = await surface.ExecuteJavaScriptAsync("return location.href + ' ' + window.documentId;", StepTimeout);
                    await surface.ExecuteJavaScriptAsync("document.getElementById('download').click(); exportBlob(); return 'ok';", StepTimeout);
                    await surface.ExecuteJavaScriptAsync("location = 'attachment.txt'; return 'ok';", StepTimeout);
                    await Task.Delay(TimeSpan.FromSeconds(1.5));
                    await surface.ExecuteJavaScriptAsync("location = 'file.bin'; return 'ok';", StepTimeout);
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    var saved = directory.GetFileSystemInfos();
                    if (saved.Length != 0)
                        throw new InvalidOperationException($"Downloads were saved without AllowDownloads: {string.Join(", ", saved.Select(file => file.Name))}.");
                    var after = await surface.ExecuteJavaScriptAsync("return location.href + ' ' + window.documentId;", StepTimeout);
                    if (after != page) throw new InvalidOperationException($"A refused download replaced the page: {after}, was {page}.");
                    Pass("without AllowDownloads, <a download>, a blob export, an attachment and an unshowable response save nothing and the page stays.");
                }
                finally { if (window.IsOpen) await window.CloseAsync(); }
            }
            // AllowDownloads: each is saved, a second copy under a new name, and the page stays.
            await using (var window = await surface.OpenWindowAsync(DownloadWindow(DesktopWebContentPolicy.Default with { AllowDownloads = true, DevTools = false })))
            {
                try
                {
                    var page = await surface.ExecuteJavaScriptAsync("return location.href + ' ' + window.documentId;", StepTimeout);
                    Expect(await NativeAsync(window, webView =>
                        Decide(webView, page.Split(' ')[0] + "file.bin", MainFrame.Main, LinkActivated, expected: 2, download: true)
                        ?? Respond(webView, canShow: false, expected: 2)
                        ?? Respond(webView, canShow: true, expected: 1)));
                    await surface.ExecuteJavaScriptAsync("document.getElementById('download').click(); return 'ok';", StepTimeout);
                    await WaitForFileAsync(directory, "runic-download.bin", FileBytes);
                    await surface.ExecuteJavaScriptAsync("document.getElementById('download').click(); return 'ok';", StepTimeout);
                    await WaitForFileAsync(directory, "runic-download (1).bin", FileBytes);
                    await surface.ExecuteJavaScriptAsync("exportBlob(); return 'ok';", StepTimeout);
                    await WaitForFileAsync(directory, "runic-export.csv", "a,b\n"u8.ToArray());
                    await surface.ExecuteJavaScriptAsync("location = 'attachment.txt'; return 'ok';", StepTimeout);
                    await WaitForFileAsync(directory, "notes.txt", FileBytes);
                    var after = await surface.ExecuteJavaScriptAsync("return location.href + ' ' + window.documentId;", StepTimeout);
                    if (after != page) throw new InvalidOperationException($"A download replaced the page: {after}, was {page}.");
                    var names = directory.GetFiles().Select(file => file.Name).Order(StringComparer.Ordinal).ToArray();
                    string[] expected = ["notes.txt", "runic-download (1).bin", "runic-download.bin", "runic-export.csv"];
                    if (!names.SequenceEqual(expected))
                        throw new InvalidOperationException($"Expected the downloads {string.Join(", ", expected)}; found {string.Join(", ", names)}.");
                    var unquarantined = names.Where(name => !HasQuarantine(Path.Combine(directory.FullName, name))).ToArray();
                    if (unquarantined.Length != 0)
                        throw new InvalidOperationException($"Downloads without com.apple.quarantine: {string.Join(", ", unquarantined)}.");
                    Pass("with AllowDownloads, <a download>, a blob export and an attachment are saved with the quarantine, a second copy as 'name (1)', and the page stays.");
                }
                finally { if (window.IsOpen) await window.CloseAsync(); }
            }
        }
        finally
        {
            MacOsWkWebViewHost.DownloadDirectory = originalDirectory;
            directory.Delete(recursive: true);
        }
    }

    private static DesktopWindowOptions DownloadWindow(DesktopWebContentPolicy policy) => new()
    {
        Browser = BrowserKind.Embedded,
        Width = 640,
        Height = 480,
        WebContent = policy,
    };

    // Waits until the file is complete and marked, as WebKit writes it and the download delegate finishes it.
    private static async Task WaitForFileAsync(DirectoryInfo directory, string name, byte[] content)
    {
        var path = Path.Combine(directory.FullName, name);
        var deadline = DateTime.UtcNow + StepTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content) && HasQuarantine(path)) return;
            }
            catch (IOException)
            {
                // Still being written.
            }
            await Task.Delay(100);
        }
        var found = directory.GetFiles().Select(file => $"{file.Name} ({file.Length} bytes{(HasQuarantine(file.FullName) ? ", quarantined" : "")})");
        throw new TimeoutException($"Timed out waiting for the download {name}; the folder has [{string.Join(", ", found)}].");
    }

    private static bool HasQuarantine(string path) => GetXattr(path, "com.apple.quarantine", 0, 0, 0, 0x0001 /* XATTR_NOFOLLOW */) >= 0;

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getxattr", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetXattr(string path, string name, nint value, nuint size, uint position, int options);

    private enum MainFrame { None, Main, Sub }

    // The stand-in objects' answers, read on the main thread while the delegate runs.
    private static nint s_frame, s_request, s_navigationType, s_policy;
    private static bool s_isMainFrame, s_download, s_canShow;
    private static int s_handlerCalls;

    private static unsafe string? Decide(nint webView, string url, MainFrame frame, nint type, nint expected, bool download = false)
    {
        var action = Send(Send(Stub("RunicSmokeNavigationAction"), Sel("alloc")), Sel("init"));
        var frameInfo = frame == MainFrame.None ? 0 : Send(Send(Stub("RunicSmokeFrameInfo"), Sel("alloc")), Sel("init"));
        try
        {
            s_frame = frameInfo;
            s_isMainFrame = frame == MainFrame.Main;
            s_navigationType = type;
            s_download = download;
            s_request = Arg(Class("NSURLRequest"), Sel("requestWithURL:"), Arg(Class("NSURL"), Sel("URLWithString:"), String(url)));
            var called = CallHandler(handler => ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)MessageSend)(
                Send(webView, Sel("navigationDelegate")), Sel("webView:decidePolicyForNavigationAction:decisionHandler:"), webView, action, handler));
            return called is { } failure ? $"{url} ({frame}, type {type}): {failure}"
                : s_policy != expected ? $"{url} ({frame}, type {type}): policy {s_policy}, expected {expected}" : null;
        }
        finally
        {
            Send(action, Sel("release"));
            if (frameInfo != 0) Send(frameInfo, Sel("release"));
            s_frame = s_request = 0;
        }
    }

    private static unsafe string? Respond(nint webView, bool canShow, nint expected)
    {
        var response = Send(Send(Stub("RunicSmokeNavigationResponse"), Sel("alloc")), Sel("init"));
        try
        {
            s_canShow = canShow;
            var called = CallHandler(handler => ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)MessageSend)(
                Send(webView, Sel("navigationDelegate")), Sel("webView:decidePolicyForNavigationResponse:decisionHandler:"), webView, response, handler));
            return called is { } failure ? $"response canShowMIMEType={canShow}: {failure}"
                : s_policy != expected ? $"response canShowMIMEType={canShow}: policy {s_policy}, expected {expected}" : null;
        }
        finally { Send(response, Sel("release")); }
    }

    private static unsafe string? CreateWebView(nint webView, string url)
    {
        var action = Send(Send(Stub("RunicSmokeNavigationAction"), Sel("alloc")), Sel("init"));
        try
        {
            s_frame = 0;
            s_navigationType = Other;
            s_download = false;
            s_request = Arg(Class("NSURLRequest"), Sel("requestWithURL:"), Arg(Class("NSURL"), Sel("URLWithString:"), String(url)));
            var configuration = Send(webView, Sel("configuration"));
            var created = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>)MessageSend)(
                Send(webView, Sel("UIDelegate")), Sel("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:"),
                webView, configuration, action, 0);
            return created != 0 ? $"window.open {url} created a web view" : null;
        }
        finally
        {
            Send(action, Sel("release"));
            s_request = 0;
        }
    }

    // A decision handler: a global block whose invoke records the policy. The delegate must call it exactly once.
    private static unsafe string? CallHandler(Action<nint> send)
    {
        var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        try
        {
            descriptor->Size = (nuint)sizeof(BlockLiteral);
            block->Isa = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock");
            block->Flags = 1 << 28; // BLOCK_IS_GLOBAL
            block->Invoke = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&RecordPolicy;
            block->Descriptor = (nint)descriptor;
            s_handlerCalls = 0;
            s_policy = -1;
            send((nint)block);
            return s_handlerCalls == 1 ? null : $"the decision handler ran {s_handlerCalls} times";
        }
        finally
        {
            NativeMemory.Free(block);
            NativeMemory.Free(descriptor);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RecordPolicy(nint block, nint policy)
    {
        s_policy = policy;
        s_handlerCalls++;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral { public nint Isa; public int Flags, Reserved; public nint Invoke, Descriptor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor { public nuint Reserved, Size; }

    // The stand-in classes answer the selectors the delegate reads.
    private static unsafe nint Stub(string name)
    {
        var existing = Class(name);
        if (existing != 0) return existing;
        var type = AllocateClassPair(Class("NSObject"), name, 0);
        var boolean = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B@:" : "c@:";
        switch (name)
        {
            case "RunicSmokeNavigationAction":
                AddMethod(type, Sel("targetFrame"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&TargetFrame, "@@:");
                AddMethod(type, Sel("request"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&Request, "@@:");
                AddMethod(type, Sel("navigationType"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&NavigationType, "q@:");
                AddMethod(type, Sel("shouldPerformDownload"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&ShouldPerformDownload, boolean);
                break;
            case "RunicSmokeFrameInfo":
                AddMethod(type, Sel("isMainFrame"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&IsMainFrame, boolean);
                break;
            case "RunicSmokeNavigationResponse":
                AddMethod(type, Sel("canShowMIMEType"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&CanShowMimeType, boolean);
                AddMethod(type, Sel("isForMainFrame"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&IsMainFrame, boolean);
                break;
        }
        RegisterClassPair(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static nint TargetFrame(nint self, nint selector) => s_frame;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static nint Request(nint self, nint selector) => s_request;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static nint NavigationType(nint self, nint selector) => s_navigationType;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static byte ShouldPerformDownload(nint self, nint selector) => s_download ? (byte)1 : (byte)0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static byte IsMainFrame(nint self, nint selector) => s_isMainFrame ? (byte)1 : (byte)0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static byte CanShowMimeType(nint self, nint selector) => s_canShow ? (byte)1 : (byte)0;

    // isInspectable, or null before macOS 13.3.
    private static unsafe bool? Inspectable(nint webView) =>
        webView == 0 || ((delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)MessageSend)(webView, Sel("respondsToSelector:"), Sel("isInspectable")) == 0
            ? null
            : ((delegate* unmanaged[Cdecl]<nint, nint, byte>)MessageSend)(webView, Sel("isInspectable")) != 0;

    // Runs a check on the main thread with the window's web view, inside an autorelease pool.
    private static async Task<string?> NativeAsync(DesktopWindow window, Func<nint, string?> check)
    {
        string? failure = null;
        await window.InvokeNativeAsync(handle =>
        {
            var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
            try { failure = check(Arg(Send(Send(handle, Sel("contentView")), Sel("subviews")), Sel("objectAtIndex:"), 0)); }
            catch (Exception error) { failure = error.ToString(); }
            finally { Send(pool, Sel("drain")); }
        });
        return failure;
    }

    private static void Expect(string? failure)
    {
        if (failure is not null) throw new InvalidOperationException("Web content: " + failure);
    }

    private static void ExpectNothingOpened(string what)
    {
        if (!Opened.IsEmpty) throw new InvalidOperationException($"{what} opened {string.Join(", ", Opened)} in the browser.");
    }

    private static void Pass(string message) => Console.WriteLine("PASS web content: " + message);

    // An autoreleased NSString, which copies the UTF-8 text.
    private static nint String(string value)
    {
        var text = Marshal.StringToCoTaskMemUTF8(value);
        try { return Arg(Class("NSString"), Sel("stringWithUTF8String:"), text); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    private static nint MessageSend { get; } = OperatingSystem.IsMacOS() ? NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend") : 0;

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)] private static partial nint AllocateClassPair(nint superclass, string name, nuint extra);
    [LibraryImport(ObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddMethod(nint type, nint selector, nint implementation, string types);
    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")] private static partial void RegisterClassPair(nint type);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Arg(nint target, nint selector, nint value);

    private const string DownloadPage = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic download smoke</title>
        <script>
        window.documentId = Date.now().toString(36) + Math.random().toString(36);
        function exportBlob() {
          const link = document.createElement('a');
          link.href = URL.createObjectURL(new Blob(['a,b\n'], { type: 'text/csv' }));
          link.download = 'runic-export.csv';
          document.body.append(link);
          link.click();
          link.remove();
        }
        </script></head>
        <body><main><h1>Downloads</h1><a id="download" download="runic-download.bin" href="file.bin">Download</a></main></body></html>
        """;

    private const string Page = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic web content smoke</title>
        <script>window.documentId = Date.now().toString(36) + Math.random().toString(36);</script></head>
        <body><main><h1>Web content</h1><a href="https://example.org/">External</a></main></body></html>
        """;
}
