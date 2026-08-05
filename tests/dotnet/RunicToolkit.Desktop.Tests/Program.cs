using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.Desktop;
using RunicToolkit.Hosting;
using RunicToolkit.Hosting.CsWebUi;

namespace RunicToolkit.Desktop.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("capability reports are complete and defensive", CapabilityReports),
            ("CsWebUi services project browser bridge operations", BrowserBridgeServices),
            ("guarded close cancels only after acceptance", GuardedClose),
            ("secondary windows are owned and deterministically released", OwnedWindows),
        ];

        foreach ((string name, Func<Task> run) in tests)
        {
            try
            {
                await run().ConfigureAwait(false);
                Console.WriteLine($"ok - {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"not ok - {name}");
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        return 0;
    }

    private static Task CapabilityReports()
    {
        DesktopCapabilityDescriptor[] descriptors = CreateDescriptors();
        var report = new DesktopCapabilityReport("test", "linux", descriptors);
        Equal(Enum.GetValues<DesktopCapability>().Length, report.Capabilities.Count);
        True(report[DesktopCapability.Clipboard].IsSupported);
        Throws<ArgumentException>(() =>
            _ = new DesktopCapabilityReport("test", "linux", descriptors[..^1]));
        Throws<ArgumentException>(() =>
            _ = new DesktopCapabilityReport(
                "test",
                "linux",
                [.. descriptors, descriptors[0]]));
        return Task.CompletedTask;
    }

    private static async Task BrowserBridgeServices()
    {
        var profile = new DesktopBrowserProfile(
            "tests",
            System.IO.Path.GetFullPath("obj/desktop-profile"));
        using var services = new CsWebUiDesktopServices(profile);
        var host = new FakeHost();
        var window = new FakeWindow();
        services.Attach(host, window);

        Equal(profile, ((IDesktopBrowserProfile)services).Current);
        Equal("clipboard", await ((IDesktopClipboard)services).ReadTextAsync());
        await ((IDesktopClipboard)services).WriteTextAsync("updated");
        Equal("clipboard.write", window.LastOperation);
        Equal("updated", await ((IDesktopBrowserStorage)services).ReadAsync("theme"));
        await ((IDesktopBrowserStorage)services).WriteAsync("theme", "dark");
        await ((IDesktopBrowserStorage)services).RemoveAsync("theme");

        IReadOnlyList<DesktopFile> files = await ((IDesktopFileDialogs)services).OpenAsync(
            new DesktopOpenFileOptions(
                "Open",
                [new DesktopFileType("Text", [".txt"])]));
        Equal(1, files.Count);
        Equal("todo.txt", files[0].Name);
        Equal(3, files[0].Content.Length);
        True(await ((IDesktopFileDialogs)services).SaveAsync(
            new DesktopSaveFileOptions(
                "Save",
                "todo.txt",
                [new DesktopFileType("Text", [".txt"])]),
            new byte[] { 1, 2, 3 }));
        await ((IDesktopNotifications)services).ShowAsync(
            new DesktopNotification("Saved", "The file was saved."));

        var acceleratorObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable accelerator = await ((IDesktopKeyboardAccelerators)services)
            .RegisterAsync(
                new DesktopKeyboardAccelerator("s", control: true),
                _ =>
                {
                    acceleratorObserved.TrySetResult();
                    return ValueTask.CompletedTask;
                });
        window.RaiseEvent("accelerator", window.LastRegisteredAccelerator!, "{}");
        await acceleratorObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        DesktopDrop? observedDrop = null;
        EventHandler<DesktopDrop> handler = (_, drop) => observedDrop = drop;
        ((IDesktopDropTarget)services).Dropped += handler;
        window.RaiseEvent(
            "drop",
            "root",
            """{"files":[{"name":"drop.txt","mediaType":"text/plain","content":"AQI="}],"text":"hello"}""");
        Equal("hello", observedDrop!.Text);
        Equal(2, observedDrop.Files[0].Content.Length);
        ((IDesktopDropTarget)services).Dropped -= handler;

        await services.DetachAsync(window, CancellationToken.None);
        Equal(
            DesktopCapabilityStatus.Unavailable,
            services.Report[DesktopCapability.Clipboard].Status);
    }

    private static async Task GuardedClose()
    {
        using var services = new CsWebUiDesktopServices(profile: null);
        var host = new FakeHost();
        var window = new FakeWindow();
        services.Attach(host, window);
        var guard = new RecordingGuard(allow: false);
        using IDisposable registration = services.RegisterCloseGuard(guard);

        DesktopCloseDecision denied = await services.RequestCloseAsync();
        False(denied.IsAllowed);
        False(services.Stopping.IsCancellationRequested);
        Equal(0, window.CloseCount);

        guard.Allow = true;
        DesktopCloseDecision accepted = await services.RequestCloseAsync();
        True(accepted.IsAllowed);
        True(services.Stopping.IsCancellationRequested);
        Equal(1, window.CloseCount);
    }

    private static async Task OwnedWindows()
    {
        using var services = new CsWebUiDesktopServices(profile: null);
        var host = new FakeHost();
        var primary = new FakeWindow();
        services.Attach(host, primary);

        IDesktopOwnedWindow owned = await ((IDesktopWindowManager)services).OpenAsync(
            "details",
            "Task details",
            new Uri("app://test/details.html"),
            new DesktopSize(640, 480));
        Equal("details", owned.Id);
        Equal(1, host.CreatedWindows.Count);
        Equal("details", host.LastOptions!.WindowId);
        Equal(640, host.LastOptions.Width);
        Equal(new Uri("app://test/details.html"), host.CreatedWindows[0].EntryPoint);
        Equal(1, host.CreatedWindows[0].ShowCount);

        await ThrowsAsync<InvalidOperationException>(
            () => ((IDesktopWindowManager)services).OpenAsync(
                "details",
                "Duplicate",
                new Uri("app://test/duplicate.html"),
                new DesktopSize(320, 240)).AsTask());

        await owned.CloseAsync();
        Equal(1, host.CreatedWindows[0].CloseCount);
        Equal(1, host.CreatedWindows[0].DisposeCount);

        IDesktopOwnedWindow reopened = await ((IDesktopWindowManager)services).OpenAsync(
            "details",
            "Reopened",
            new Uri("app://test/reopened.html"),
            new DesktopSize(800, 600));
        await services.DetachAsync(primary, CancellationToken.None);
        Equal(1, host.CreatedWindows[1].CloseCount);
        Equal(1, host.CreatedWindows[1].DisposeCount);
        await reopened.DisposeAsync();
    }

    private static DesktopCapabilityDescriptor[] CreateDescriptors()
    {
        var descriptors = new List<DesktopCapabilityDescriptor>();
        foreach (DesktopCapability capability in Enum.GetValues<DesktopCapability>())
        {
            descriptors.Add(new(capability, DesktopCapabilityStatus.Supported));
        }

        return descriptors.ToArray();
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool condition) => True(!condition);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class RecordingGuard(bool allow) : IDesktopCloseGuard
    {
        internal bool Allow { get; set; } = allow;

        public ValueTask<DesktopCloseDecision> CanCloseAsync(
            DesktopCloseRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                Allow
                    ? DesktopCloseDecision.Allow()
                    : DesktopCloseDecision.Deny("unsaved"));
        }
    }

    private sealed class FakeHost : IBrowserHost
    {
        public IUiDispatcher Dispatcher { get; } = new InlineDispatcher();

        internal List<FakeWindow> CreatedWindows { get; } = [];
        internal BrowserWindowOptions? LastOptions { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<IBrowserWindow> CreateWindowAsync(
            BrowserWindowOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastOptions = options;
            var window = new FakeWindow();
            CreatedWindows.Add(window);
            return ValueTask.FromResult<IBrowserWindow>(window);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWindow : IBrowserWindow, IBrowserWindowDesktopAdapter
    {
        private readonly TaskCompletionSource _closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler? CloseRequested
        {
            add
            {
            }
            remove
            {
            }
        }
        public event EventHandler<BrowserDesktopEventArgs>? DesktopEventReceived;

        public DesktopCapabilityReport Capabilities { get; } =
            new("test", "linux", CreateDescriptors());

        internal string? LastOperation { get; private set; }
        internal string? LastRegisteredAccelerator { get; private set; }
        internal int CloseCount { get; private set; }
        internal int ShowCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal Uri? EntryPoint { get; private set; }

        public ValueTask<string> InvokeBrowserAsync(
            string operation,
            string payloadJson,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastOperation = operation;
            if (operation == "accelerator.register")
            {
                using System.Text.Json.JsonDocument document =
                    System.Text.Json.JsonDocument.Parse(payloadJson);
                LastRegisteredAccelerator =
                    document.RootElement.GetProperty("id").GetString();
            }

            string result = operation switch
            {
                "clipboard.read" => "\"clipboard\"",
                "storage.read" => "\"updated\"",
                "files.open" =>
                    """[{"name":"todo.txt","mediaType":"text/plain","content":"AQID"}]""",
                "files.save" => "true",
                _ => "null",
            };
            return ValueTask.FromResult(result);
        }

        internal void RaiseEvent(string name, string id, string payload) =>
            DesktopEventReceived?.Invoke(this, new BrowserDesktopEventArgs(name, id, payload));

        public ValueTask FocusWindowAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask FocusElementAsync(string elementId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SetSizeAsync(DesktopSize size, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SetPositionAsync(DesktopPosition position, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask CenterAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SetStateAsync(DesktopWindowState state, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask NavigateAsync(Uri entryPoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EntryPoint = entryPoint;
            return ValueTask.CompletedTask;
        }

        public ValueTask ShowAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShowCount++;
            return ValueTask.CompletedTask;
        }

        public Task WaitForCloseAsync(CancellationToken cancellationToken) =>
            _closed.Task.WaitAsync(cancellationToken);

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseCount++;
            _closed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _closed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public ValueTask InvokeAsync(
            Func<CancellationToken, ValueTask> callback,
            CancellationToken cancellationToken) =>
            callback(cancellationToken);

        public ValueTask<TResult> InvokeAsync<TResult>(
            Func<CancellationToken, ValueTask<TResult>> callback,
            CancellationToken cancellationToken) =>
            callback(cancellationToken);
    }
}
