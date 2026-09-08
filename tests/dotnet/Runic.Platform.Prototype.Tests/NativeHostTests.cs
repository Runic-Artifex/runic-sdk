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
    internal static int Run(bool manualSelection = false)
    {
        using var watchdog = new Timer(_ => { Console.Error.WriteLine("FAIL native platform test exceeded 90 seconds."); Environment.Exit(1); },
            null, TimeSpan.FromSeconds(manualSelection ? 240 : 90), Timeout.InfiniteTimeSpan);
        DesktopApplicationHost? host = null;
        PresentationLifetime? lifetime = null;
        var owner = new DesktopNativeOwner(() => host?.Window);
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
            Window = new() { Browser = BrowserKind.Embedded, Width = 640, Height = 480 },
            Surface = new() { Content = """<!doctype html><html><head><script src="webui.js"></script><title>Runic native platform tests</title></head><body>Native platform conformance</body></html>""" },
        });
        var builder = new RunicApplicationBuilder(new("platform.native", "0.0.0", "test", []), []);
        builder.UseHost(host);
        var app = builder.Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(manualSelection ? 210 : 60));
        Task exercise = Task.Run(async () =>
        {
            try
            {
                while (!owner.IsAvailable) await Task.Delay(10, deadline.Token);
                var active = lifetime ?? throw new InvalidOperationException("No live scoped lifetime.");
                await ExerciseAsync(host, owner, active, manualSelection, deadline.Token);
            }
            finally { await deadline.CancelAsync(); }
        });
        try
        {
            try { app.Run(deadline.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            exercise.GetAwaiter().GetResult();
            Console.WriteLine("PASS native owner dispatch, picker cancellation, close interception and scoped release.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static async Task ExerciseAsync(DesktopApplicationHost host, DesktopNativeOwner owner, PresentationLifetime lifetime, bool manualSelection, CancellationToken deadline)
    {
        var window = host.Window!;
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
