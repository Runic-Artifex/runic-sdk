using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Desktop;
using TUnit.Core;

// W315-006: files dropped onto a Runic Desktop window reach the Window's root ViewModel when it implements
// IRunicFileDropTarget, one drop after another with their native paths; a failing drop is logged.
internal sealed class FileDropChecks
{
    [Test, NotInParallel(Order = 19)]
    public async Task DropsReachTheRootViewModelInOrder()
    {
        var root = Directory.CreateTempSubdirectory("runic-drop-");
        try
        {
            var file = Path.Combine(root.FullName, "notes.txt");
            await File.WriteAllTextAsync(file, "notes");
            var logs = new RecordingLoggerProvider();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(logs));
            services.AddScoped<DropViewModel>();
            services.AddScoped<Func<IBridgeTransport, DropViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            var factory = new NativeDispatchFactory();
            await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
            var window = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<DropWindow>();
            try
            {
                var viewModel = window.ViewModel;
                var native = factory.Hosts[0];
                native.Drop(new DesktopFileDropEventArgs([file, root.FullName], "folder-7"));
                native.Drop(new DesktopFileDropEventArgs([file], "fail"));
                native.Drop(new DesktopFileDropEventArgs([file]));
                await viewModel.Third.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
                Require(viewModel.Drops.Count == 3, $"Expected three drops, got {viewModel.Drops.Count}.");
                var first = viewModel.Drops[0];
                Require(first.Target == "folder-7" && first.Files.Count == 2
                    && first.Files[0] == new RunicDroppedFile(file, false) && first.Files[1] == new RunicDroppedFile(root.FullName, true)
                    && first.Files[0].Name == "notes.txt",
                    "The first drop did not carry its paths, directory flags and label.");
                Require(viewModel.Drops[1].Target == "fail" && viewModel.Drops[2].Target is null, "Drops did not arrive in order.");
                Require(viewModel.Overlapped == 0, "Two drops ran at the same time.");
                Require(logs.Messages.Any(static message => message.Contains("DropViewModel failed with System.InvalidOperationException to handle 1 dropped files", StringComparison.Ordinal)),
                    "A failing drop was not logged as WindowFileDropFailed.");

                // A drop that is still running is cancelled when the Window closes.
                viewModel.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
                native.Drop(new DesktopFileDropEventArgs([file], "held"));
                await viewModel.HoldEntered.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
                await ((IBridgeWindow)window.Host).CloseAsync(TimeSpan.FromSeconds(5));
                Require(await viewModel.HoldCancelled.Task.WaitScaledAsync(TimeSpan.FromSeconds(5)), "Closing the Window did not cancel the drop.");
            }
            finally
            {
                await window.DisposeAsync();
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // B11 (D037.3): files without a local path reach the ViewModel as promised files with the drop's effect; the host
    // disposes the ones the handler did not open once it returns, and leaves an opened one to the handler.
    [Test, NotInParallel(Order = 19)]
    public async Task PromisedFilesAndTheEffectReachTheRootViewModel()
    {
        var services = new ServiceCollection();
        services.AddScoped<PromisedDropViewModel>();
        services.AddScoped<Func<IBridgeTransport, PromisedDropViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
        await using var provider = services.BuildServiceProvider();
        var factory = new NativeDispatchFactory();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var window = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<PromisedDropWindow>();
        try
        {
            var opened = new RecordingLease("attachment.pdf");
            var unopened = new RecordingLease("../photo.jpg");
            factory.Hosts[0].Drop(new DesktopFileDropEventArgs([], [opened, unopened], "inbox") { Effect = Runic.Platform.DesktopDragEffects.Move });
            var drop = await window.ViewModel.Received.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
            Require(drop.Files.Count == 0 && drop.Target == "inbox" && drop.Effect == RunicDropEffect.Move, "The drop lost its label or effect.");
            Require(drop.PromisedFiles.Select(static file => file.Name).SequenceEqual(["attachment.pdf", "photo.jpg"]), "The promised file names were not kept or sanitized.");
            Require(window.ViewModel.Content == "content", "The handler did not read the promised file.");
            for (var attempt = 0; attempt < 100 && unopened.Disposals == 0; attempt++) await Task.Delay(10);
            Require(unopened.Disposals == 1 && opened.Disposals == 0, "The host did not dispose only the promised file the handler left unopened.");
            await drop.PromisedFiles[0].DisposeAsync();
            Require(opened.Disposals == 1, "Disposing the opened promised file did not reach its lease.");
        }
        finally
        {
            await window.DisposeAsync();
        }
    }

    [Test, NotInParallel(Order = 19)]
    public async Task AViewModelWithoutTheInterfaceIgnoresDrops()
    {
        var services = new ServiceCollection();
        services.AddScoped<PlainViewModel>();
        services.AddScoped<Func<IBridgeTransport, PlainViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
        await using var provider = services.BuildServiceProvider();
        var factory = new NativeDispatchFactory();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        await using var window = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<PlainWindow>();
        factory.Hosts[0].Drop(new DesktopFileDropEventArgs([Path.Combine(Path.GetTempPath(), "x.txt")]));
        await Task.Delay(50);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

sealed class DropViewModel : INotifyPropertyChanged, IRunicFileDropTarget
{
    private int _running;
    public List<RunicFileDrop> Drops { get; } = [];
    public int Overlapped;
    public TaskCompletionSource Third { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? Hold { get; set; }
    public TaskCompletionSource HoldEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> HoldCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public async ValueTask OnFilesDroppedAsync(RunicFileDrop drop, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _running) != 1) Interlocked.Increment(ref Overlapped);
        try
        {
            if (drop.Target == "held" && Hold is { } hold)
            {
                HoldEntered.TrySetResult();
                try { await hold.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { HoldCancelled.TrySetResult(true); throw; }
                return;
            }
            await Task.Delay(10, CancellationToken.None);
            Drops.Add(drop);
            if (Drops.Count == 3) Third.TrySetResult();
            if (drop.Target == "fail") throw new InvalidOperationException("drop failure");
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

sealed class DropWindow : RunicWindow<DropViewModel>;

sealed class PromisedDropViewModel : INotifyPropertyChanged, IRunicFileDropTarget
{
    public TaskCompletionSource<RunicFileDrop> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? Content { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public async ValueTask OnFilesDroppedAsync(RunicFileDrop drop, CancellationToken cancellationToken)
    {
        var stream = await drop.PromisedFiles[0].OpenReadAsync(cancellationToken);
        using (var reader = new StreamReader(stream, leaveOpen: true)) Content = await reader.ReadToEndAsync(cancellationToken);
        Received.TrySetResult(drop);
    }
}

sealed class PromisedDropWindow : RunicWindow<PromisedDropViewModel>;

sealed class RecordingLease(string name) : Runic.Platform.IReadFileLease
{
    public int Disposals;
    public string DisplayName => name;
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<Stream>(new MemoryStream("content"u8.ToArray()));
    public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
}

sealed class PlainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

sealed class PlainWindow : RunicWindow<PlainViewModel>;

sealed class RecordingLoggerProvider : ILoggerProvider
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this);
    public void Dispose() { }

    private sealed class Logger(RecordingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider.Messages.Enqueue(formatter(state, exception));
    }
}
