using Runic.Platform.Windows;
using Runic.Platform.Linux;
using Runic.Platform.MacOS;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application;
using Runic.Application.Bridge;
using Runic.Application.Desktop;
using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.Runtime;
using Runic.Application.Platform;
using Runic.Application.Platform.Desktop;

internal static class NativeHostTests
{
    // Synchronous entry: Application.Run owns AppKit's process-main-thread pump.
    internal static int Run(bool manualSelection = false, bool manualDesktopServices = false)
    {
        using var watchdog = new Timer(_ => { Console.Error.WriteLine("FAIL native platform test exceeded its watchdog deadline."); Environment.Exit(1); },
            null, TimeSpan.FromSeconds(manualSelection || manualDesktopServices ? 300 : 90), Timeout.InfiniteTimeSpan);
        DesktopApplicationHost? host = null;
        PresentationLifetime? lifetime = null;
        var owner = new DesktopNativeOwner(() => host?.Window);
        var startup = System.Diagnostics.Stopwatch.StartNew();
        RunicApplicationBridgeCompositionRegistry.Register(services =>
        {
            services.AddRunicPlatform(_ => new PlatformProvider { OwnerAvailable = () => owner.IsAvailable, Generation = owner.Generation });
            services.AddScoped<IApplicationBridgeDispatcher>(provider =>
            {
                lifetime = provider.GetRequiredService<PresentationLifetime>();
                if (OperatingSystem.IsMacOS())
                {
                    // This factory runs on Application.Run's worker before the
                    // first window opens. Discovery must recognize the main loop.
                    Check(DesktopPlatform.IsEmbeddedWindowAvailable,
                        "macOS discovery rejected the active Application event loop.");
                    Console.WriteLine("Native: macOS worker discovery accepted the active main-thread loop.");
                }
                return new EmptyDispatcher();
            });
        }, ApplicationBridgeSessionFactory.Create);
        host = new DesktopApplicationHost(new()
        {
            Host = new() { DiagnosticSink = diagnostic => Console.WriteLine($"Desktop: {diagnostic.Code}: {diagnostic.Message}") },
            Window = new()
            {
                Browser = BrowserKind.Embedded, Width = 640, Height = 480,
                ConfirmCloseAsync = async token => await host!.Surface!.ExecuteJavaScriptAsync(
                    "return await globalThis.__runicConfirmClose();", TimeSpan.FromSeconds(10), cancellationToken: token) == "true",
            },
            Surface = new()
            {
                Content = PublicTransportPage(),
                ContentHandler = (request, _) =>
                {
                    var phase = request.Path switch
                    {
                        "/" => "document-requested",
                        "/__native-startup/bootstrap-present" => "bootstrap-present",
                        "/__native-startup/bootstrap-missing" => "bootstrap-missing",
                        "/__native-startup/module-start" => "module-start",
                        "/__native-startup/script-error" => "script-error",
                        "/__native-startup/unhandled-rejection" => "unhandled-rejection",
                        "/__native-startup/channel-connected" => "channel-connected",
                        "/__native-startup/channel-failed" => "channel-failed",
                        _ => null,
                    };
                    if (phase is not null)
                        Console.WriteLine($"Native startup +{startup.Elapsed.TotalMilliseconds:F0}ms: {phase}");
                    return ValueTask.FromResult<ContentResponse?>(phase is not null && request.Path != "/"
                        ? new ContentResponse(ReadOnlyMemory<byte>.Empty) : null);
                },
            },
        });
        var builder = new RunicApplicationBuilder(new("platform.native", "0.0.0", "test", []), []);
        builder.UseHost(host);
        Task exercise = Task.CompletedTask;
        if (manualDesktopServices) builder.Services.AddSingleton<IApplicationStoppingParticipant>(new DrainDesktopServices(() => exercise));
        var app = builder.Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(manualSelection || manualDesktopServices ? 270 : 60));
        exercise = Task.Run(async () =>
        {
            try
            {
                while (!owner.IsAvailable) await Task.Delay(10, deadline.Token);
                var active = lifetime ?? throw new InvalidOperationException("No live scoped lifetime.");
                if (manualDesktopServices) await DesktopServicesNativeSmoke.RunAsync(owner, deadline.Token);
                else await ExerciseAsync(host, owner, active, manualSelection, deadline.Token);
            }
            finally { await deadline.CancelAsync(); }
        });
        try
        {
            try { app.Run(deadline.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            exercise.GetAwaiter().GetResult();
            Console.WriteLine(manualDesktopServices
                ? "PASS requested desktop service API checks and manual confirmation."
                : "PASS native owner dispatch, picker cancellation, close interception and scoped release.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private sealed class DrainDesktopServices(Func<Task> pending) : IApplicationStoppingParticipant
    {
        public async ValueTask StopAsync() => await pending().ConfigureAwait(false);
    }

    private static async Task ExerciseAsync(DesktopApplicationHost host, DesktopNativeOwner owner, PresentationLifetime lifetime, bool manualSelection, CancellationToken deadline)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            await using var settings = OperatingSystem.IsWindows() ? WindowsPlatformProvider.CreateSettings() : MacOSPlatformProvider.CreateSettings();
            var appearance = await settings.ReadAsync(deadline);
            Check(appearance is PlatformResult<DesktopAppearance>.Success, $"Native appearance API failed: {appearance}");
            Console.WriteLine($"Native desktop appearance: {appearance}");
        }

        var window = host.Window!;
        await ExercisePublicTransportCloseAsync(host, deadline);
        await owner.InvokeAsync(handle =>
        {
            Check(handle != 0 && window.CheckNativeAccess(), "Native dispatch did not use the actual owner's thread.");
            var nested = owner.InvokeAsync(_ => Check(window.CheckNativeAccess(), "Nested dispatch lost owner affinity."));
            Check(nested.IsCompletedSuccessfully, "Nested native dispatch was not inline.");
            nested.AsTask().GetAwaiter().GetResult();
        }, deadline);
        if (manualSelection)
        {
            Console.WriteLine("MANUAL: select a readable file outside the app sandbox; its bytes will not be printed.");
            var manual = new PresentationFiles(lifetime, new NativePickerBackend(owner, NewPicker(owner).Picker));
            var selectedResult = await manual.OpenFileAsync(new(), deadline);
            if (selectedResult is not PickerResult<IReadFileLease>.Selected selected)
                throw new InvalidOperationException("Manual native selection must select a file to prove acquired access.");
            await using (var lease = selected.Value)
            {
                var stream = await lease.OpenReadAsync(deadline);
                byte[] sample = new byte[4096];
                _ = await stream.ReadAsync(sample, deadline);
            }
            Console.WriteLine("PASS manual native selection: acquired file read and lease release completed.");
        }
        var (picker, shown) = NewPicker(owner);
        var files = new PresentationFiles(lifetime, new NativePickerBackend(owner, picker));
        using var cancel = new CancellationTokenSource();
        Console.WriteLine("Native: opening owned file picker.");
        var open = files.OpenFileAsync(new(), cancel.Token).AsTask();
        await shown.WaitAsync(deadline);
        await owner.InvokeAsync(_ => { }, deadline); // A visible dialog must still service owner work.
        Console.WriteLine("Native: cancelling open picker on owner thread.");
        await cancel.CancelAsync();
        try { await open.WaitAsync(deadline); throw new InvalidOperationException("Cancelled picker returned selection/dismissal."); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        Check(window.IsOpen, "Cancelling a picker closed its owner.");

        string directory = Path.Combine(Path.GetTempPath(), $"runic-native-access-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "contact.txt");
        await File.WriteAllTextAsync(path, "selected stream", deadline);
        try
        {
            // Actual acquired stream plus an owner-thread release. This exercises
            // teardown, not a claim that CI granted sandbox permissions.
            var release = new OwnerRelease(owner);
            var resource = new ReadFileLease("contact.txt", new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), release);
            var facade = new PresentationFiles(lifetime, new SelectedBackend(resource));
            var selected = (PickerResult<IReadFileLease>.Selected)await facade.OpenFileAsync(new(), deadline);
            var stream = await selected.Value.OpenReadAsync(deadline);
            (picker, shown) = NewPicker(owner);
            files = new PresentationFiles(lifetime, new NativePickerBackend(owner, picker));
            Console.WriteLine("Native: opening owned save picker.");
            var save = files.SaveFileAsync(new("export.json"), deadline).AsTask();
            await shown.WaitAsync(deadline);
            Console.WriteLine("Native: requesting owner close with save picker active.");
            var close = window.RequestCloseAsync(deadline).AsTask();
            Check(await save.WaitAsync(deadline) is PickerResult<ISaveFileLease>.Unavailable { Reason: UnavailableReason.OwnerClosed },
                "Owner shutdown did not cancel its native save panel.");
            Check(await close.WaitAsync(deadline), "Native close interception was denied.");
            Check(!stream.CanRead && release.Releases == 1, "Native shutdown leaked the lease stream or owner-thread release.");
            await selected.Value.DisposeAsync();
            Check(release.Releases == 1 && !window.IsOpen, "Release was repeated or the owner remained open.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static string PublicTransportPage()
    {
        using var stream = typeof(NativeHostTests).Assembly.GetManifestResourceStream("Runic.Platform.NativeHost.Frontend")
            ?? throw new InvalidOperationException("The public Desktop transport fixture was not embedded.");
        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd().Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
        return "<!doctype html><html><head><meta charset=\"utf-8\"><script src=\"runic-desktop.js\"></script>" +
            "<script>globalThis.__nativePhase=(s)=>fetch('__native-startup/'+s).catch(()=>{});" +
            "addEventListener('error',()=>__nativePhase('script-error'));" +
            "addEventListener('unhandledrejection',()=>__nativePhase('unhandled-rejection'));" +
            "__nativePhase(typeof runicDesktop==='object'?'bootstrap-present':'bootstrap-missing');</script>" +
            "<title>Runic native platform tests</title></head><body>Native platform conformance<script type=\"module\">" +
            "__nativePhase('module-start');" + script + "</script></body></html>";
    }

    private static async Task ExercisePublicTransportCloseAsync(DesktopApplicationHost host, CancellationToken deadline)
    {
        var surface = host.Surface!;
        var window = host.Window!;
        Task<string> Script(string source) => surface.ExecuteJavaScriptAsync(source, TimeSpan.FromSeconds(5), cancellationToken: deadline);
        Check(await Script("return 'native public transport ✓';") == "native public transport ✓",
            "The public Desktop transport did not return an authenticated JavaScript result.");
        try
        {
            await Script("throw new Error('native-script-error-probe');");
            throw new InvalidOperationException("A JavaScript failure was reported as success.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("native-script-error-probe", StringComparison.Ordinal)) { }
        await surface.RunJavaScriptAsync("globalThis.__runicCloseProbe.quick++;", deadline);
        Check(await Script("return globalThis.__runicCloseProbe.quick;") == "1", "Quick JavaScript execution was ignored.");

        await ExercisePublicTransportNavigationAsync(surface, deadline);

        // Model a dirty frontend draft whose confirmation is asynchronous. A second
        // script must still round-trip while the close decision awaits user input.
        var veto = window.RequestCloseAsync(deadline).AsTask();
        while (await Script("return globalThis.__runicCloseProbe.pending;") != "true")
            await Task.Delay(10, deadline);
        Check(!veto.IsCompleted && window.IsOpen, "Dirty frontend confirmation did not remain pending.");
        Check(await Script("return globalThis.__runicCloseProbe.calls;") == "1", "Close confirmation was replayed.");
        await surface.RunJavaScriptAsync("globalThis.__runicCloseProbe.resolve(false);", deadline);
        Check(!await veto.WaitAsync(deadline) && window.IsOpen, "A frontend veto closed the native owner.");
        Check(await Script("return globalThis.__runicCloseProbe.pending;") == "false", "Veto did not finish frontend confirmation.");
        // The existing picker/shutdown scenario retries close and must now be approved
        // through this same public transport before the presentation scope is drained.
        await surface.RunJavaScriptAsync("globalThis.__runicCloseProbe.allow = true;", deadline);
        Console.WriteLine("PASS public Desktop transport: script result/error/quick execution, native document navigation, asynchronous close veto, and retry readiness.");
    }

    private static async Task ExercisePublicTransportNavigationAsync(DesktopSurface surface, CancellationToken deadline)
    {
        var previousDocument = await surface.ExecuteJavaScriptAsync("return globalThis.__runicCloseProbe.documentId;",
            TimeSpan.FromSeconds(5), cancellationToken: deadline);
        string query = "nativeNavigation=" + Guid.NewGuid().ToString("N");
        string destination = new UriBuilder(surface.Url) { Query = query }.Uri.AbsoluteUri;
        using var navigation = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        navigation.CancelAfter(TimeSpan.FromSeconds(15));
        await surface.NavigateAsync(destination, navigation.Token);
        Exception? lastTransient = null;
        try
        {
            while (true)
            {
                navigation.Token.ThrowIfCancellationRequested();
                try
                {
                    // Both a per-document global and a newly created DOM node must
                    // change. Updating history or a transport state flag cannot pass.
                    var receipt = await surface.ExecuteJavaScriptAsync(
                        "return [location.search, globalThis.__runicCloseProbe?.documentId, document.getElementById('runic-native-document')?.textContent].join('\\n');",
                        TimeSpan.FromSeconds(1), cancellationToken: navigation.Token);
                    var parts = receipt.Split('\n');
                    if (parts.Length == 3 && parts[0] == "?" + query && parts[1] != previousDocument &&
                        Guid.TryParse(parts[1], out _) && parts[2] == parts[1])
                    {
                        Console.WriteLine("PASS public Desktop navigation: changed native document, DOM marker, URL and authenticated session.");
                        return;
                    }
                }
                catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException or System.Net.WebSockets.WebSocketException ||
                    error is InvalidOperationException && error.Message == "No authenticated WebUI browser is connected.")
                {
                    // Navigation tears down the old authenticated connection before
                    // the replacement document creates its public transport.
                    lastTransient = error;
                }
                await Task.Delay(25, navigation.Token);
            }
        }
        catch (OperationCanceledException) when (navigation.IsCancellationRequested && !deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Native navigation did not create and authenticate a new frontend document within 15 seconds.", lastTransient);
        }
    }

    private static (INativeFilePicker Picker, Task Shown) NewPicker(DesktopNativeOwner owner)
    {
        if (OperatingSystem.IsWindows()) { var picker = new WindowsFilePicker(owner); return (picker, picker.Shown.Task); }
        if (OperatingSystem.IsMacOS()) { var picker = new MacOsFilePicker(owner); return (picker, picker.Shown.Task); }
        if (OperatingSystem.IsLinux()) { var picker = new LinuxFilePicker(owner); return (picker, picker.Shown.Task); }
        throw new PlatformNotSupportedException();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class OwnerRelease(DesktopNativeOwner owner) : IAsyncDisposable
    {
        internal int Releases;
        public ValueTask DisposeAsync() => owner.InvokeAsync(_ => Releases++);
    }
    private sealed class SelectedBackend(IReadFileLease lease) : IPickerBackend
    {
        public bool IsAvailable => true;
        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PickerResult<IReadFileLease>>(new PickerResult<IReadFileLease>.Selected(lease));
        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class EmptyDispatcher : IApplicationBridgeDispatcher
    {
        public string ProtocolIdentity => "runic.platform.native";
        public int ProtocolVersion => 1;
        public string ManifestFingerprint => new('a', 64);
        public ValueTask<JsonElement> GetSnapshotAsync(BridgeSnapshotContext context, CancellationToken cancellationToken) => ValueTask.FromResult(TestJson.Zero);
        public ValueTask<BridgeDispatchResult> DispatchAsync(JsonElement command, BridgeCommandContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
