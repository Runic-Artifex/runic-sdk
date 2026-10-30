using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Wpf;
using Runic.Desktop;
using TUnit.Core;

// RunicViewHost's ordering, without WPF: serialised changes, cancellation, release order and fallback.
internal sealed class HostLifecycleTests
{
    // Close order: session, then Desktop host, then the native control; the owner's model is never released.
    [Test, NotInParallel("WpfSuite", Order = 5)]
    public async Task ReleasesInReverseOrder()
    {
        var lifecycle = new PresentationLifecycle();
        List<string> log = [];
        var wanted = true;
        await lifecycle.RequestAsync(() => wanted, (owner, _) =>
        {
            owner.Own(() => log.Add("control"));
            owner.Own(() => log.Add("desktop"));
            owner.Own(() => log.Add("session"));
            return Task.CompletedTask;
        });
        Program.Check(lifecycle.State == RunicViewHostState.Open && log.Count == 0, "The open did not settle as Open.");
        wanted = false;
        await lifecycle.RequestAsync(() => wanted, (_, _) => throw new InvalidOperationException("Opened while unwanted."));
        Program.Check(log.SequenceEqual(["session", "desktop", "control"]), $"Released in the order {string.Join(", ", log)}.");
        Program.Check(lifecycle.State == RunicViewHostState.Closed && lifecycle.Error is null, "Closing did not settle as Closed.");
    }

    // A swap during an open cancels it; the next open starts only after the cancelled one has released.
    [Test, NotInParallel("WpfSuite", Order = 6)]
    public async Task NewerRequestCancelsOpenAndWaits()
    {
        var lifecycle = new PresentationLifecycle();
        List<string> log = [];
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = lifecycle.RequestAsync(() => true, async (owner, token) =>
        {
            owner.Own(async () =>
            {
                await Task.Yield();
                log.Add("first released");
            });
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await started.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Program.Check(lifecycle.State == RunicViewHostState.Opening, "The open did not report Opening.");
        var second = lifecycle.RequestAsync(() => true, (owner, _) =>
        {
            log.Add("second opened");
            owner.Own(() => log.Add("second released"));
            return Task.CompletedTask;
        });
        await second.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Program.Check(first.IsCompletedSuccessfully, "The cancelled change faulted.");
        Program.Check(log.SequenceEqual(["first released", "second opened"]), $"The swap ran as {string.Join(", ", log)}.");
        Program.Check(lifecycle.State == RunicViewHostState.Open && ReferenceEquals(lifecycle.Settled, second), "The swap did not settle as Open.");
    }

    // Requests that queue behind a running change collapse into the latest one.
    [Test, NotInParallel("WpfSuite", Order = 7)]
    public async Task WaitingRequestIsSkipped()
    {
        var lifecycle = new PresentationLifecycle();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        Task Open(PresentationLifecycle owner, CancellationToken token)
        {
            opens++;
            // Ignores its token, as a slow native call would.
            return opens == 1 ? gate.Task : Task.CompletedTask;
        }
        _ = lifecycle.RequestAsync(() => true, Open);
        _ = lifecycle.RequestAsync(() => true, Open);
        var last = lifecycle.RequestAsync(() => true, Open);
        gate.SetResult();
        await last.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Program.Check(opens == 2, $"Three requests opened {opens} times instead of twice.");
        Program.Check(lifecycle.State == RunicViewHostState.Open, "The collapsed requests did not settle as Open.");
    }

    // A failed open releases what it acquired before the fallback shows; the next change removes the fallback first.
    [Test, NotInParallel("WpfSuite", Order = 8)]
    public async Task FailureReleasesThenFallsBack()
    {
        var lifecycle = new PresentationLifecycle();
        List<string> log = [];
        var failure = new PlatformNotSupportedException("No WebView2.");
        var changes = 0;
        lifecycle.Changed += () => changes++;
        await lifecycle.RequestAsync(() => true, (owner, _) =>
        {
            owner.Own(() => log.Add("control released"));
            throw failure;
        }, (owner, error) =>
        {
            log.Add($"fallback for {error.Message}");
            owner.Own(() => log.Add("fallback removed"));
        });
        Program.Check(log.SequenceEqual(["control released", "fallback for No WebView2."]), $"The failure ran as {string.Join(", ", log)}.");
        Program.Check(lifecycle.State == RunicViewHostState.Failed && ReferenceEquals(lifecycle.Error, failure) && changes == 2,
            "The failure was not reported once as Failed with its error.");
        await lifecycle.RequestAsync(() => true, (_, _) =>
        {
            log.Add("retried");
            return Task.CompletedTask;
        });
        Program.Check(log.TakeLast(2).SequenceEqual(["fallback removed", "retried"]) && lifecycle.Error is null,
            "A retry did not remove the fallback before opening.");
    }

    // RunicViewHost only knows the model at run time: the Bridge of its class or nearest base class is used.
    [Test, NotInParallel("WpfSuite", Order = 9)]
    public async Task ModelTypeResolvesFromBaseClass()
    {
        var attachment = new Attachment();
        var collection = new ServiceCollection();
        collection.AddSingleton<Func<IBridgeTransport, BaseModel, IDisposable>>((_, _) => attachment);
        await using var services = collection.BuildServiceProvider();
        await using var host = await DesktopHost.StartAsync();
        var model = new DerivedModel();
        var view = await services.CreateWpfViewForModelAsync(host, null, Program.HtmlOptions(), model, null, CancellationToken.None);
        Program.Check(ReferenceEquals(view.ViewModel, model) && view is WpfBridgeView<BaseModel>, "The base-class Bridge was not used.");
        await view.DisposeAsync();
        Program.Check(attachment.Disposed, "The session did not release its attachment.");
        try
        {
            await new ServiceCollection().BuildServiceProvider()
                .CreateWpfViewForModelAsync(host, null, Program.HtmlOptions(), model, null, CancellationToken.None);
            throw new InvalidOperationException("A model without a Bridge was accepted.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("AddRunicViews", StringComparison.Ordinal)) { }
    }

    // Failures of the fallback and of a release go to the host's logger, not to Trace, when it has one (#249).
    [Test, NotInParallel("WpfSuite", Order = 13)]
    public async Task FailuresLogThroughTheHostLogger()
    {
        var logger = new RecordingLogger();
        var lifecycle = new PresentationLifecycle { Logger = logger };
        await lifecycle.RequestAsync(() => true, (owner, _) =>
        {
            owner.Own(() => throw new System.IO.IOException("Release failed."));
            throw new PlatformNotSupportedException("No WebView2.");
        }, (_, _) => throw new InvalidOperationException("Fallback failed."));
        Program.Check(lifecycle.State == RunicViewHostState.Failed, "The failure did not settle as Failed.");
        Program.Check(logger.Entries.SequenceEqual([(1113, "ViewHostCleanupFailed", typeof(System.IO.IOException)),
                (1111, "ViewHostFallbackFailed", typeof(InvalidOperationException))]),
            $"The host logged {string.Join(", ", logger.Entries)}.");
    }

    // An application may dispose the services, or the scope, it set before the host unloads. Resolving the logger
    // must not throw, and a release that logs through a disposed factory still runs, and logs to Trace (#250).
    [Test, NotInParallel("WpfSuite", Order = 14)]
    public async Task DisposedServicesDoNotBreakARelease()
    {
        var root = new ServiceCollection().BuildServiceProvider();
        var scope = root.CreateScope();
        scope.Dispose();
        Program.Check(LentLoggerFactory.From(scope.ServiceProvider) is null, "A disposed provider lent a logger factory.");
        await root.DisposeAsync();

        var factory = new DisposableLoggerFactory();
        var lifecycle = new PresentationLifecycle { Logger = WpfViewsLog.For(LentLoggerFactory.From(factory)) };
        var released = false;
        var wanted = true;
        await lifecycle.RequestAsync(() => wanted, (owner, _) =>
        {
            owner.Own(() => released = true);
            owner.Own(() => throw new System.IO.IOException("Release failed."));
            return Task.CompletedTask;
        });
        factory.Dispose();
        using var trace = new TraceCapture();
        System.Diagnostics.Trace.Listeners.Add(trace);
        try
        {
            wanted = false;
            var release = lifecycle.RequestAsync(() => wanted, (_, _) => Task.CompletedTask);
            await release.WaitScaledAsync(TimeSpan.FromSeconds(5));
            Program.Check(release.IsCompletedSuccessfully && released && lifecycle.State == RunicViewHostState.Closed,
                "A release after the logger factory was disposed did not complete.");
            Program.Check(trace.Text.Contains("RunicViewHost presentation cleanup failed", StringComparison.Ordinal),
                $"The cleanup failure did not fall back to Trace: {trace.Text}");
        }
        finally { System.Diagnostics.Trace.Listeners.Remove(trace); }
    }

    // A logger factory whose loggers throw ObjectDisposedException once it is disposed, as a disposed provider's do.
    private sealed class DisposableLoggerFactory : ILoggerFactory
    {
        private bool _disposed;
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() => _disposed = true;

        private sealed class Logger(DisposableLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => ObjectDisposedException.ThrowIf(owner._disposed, owner);
        }
    }

    private sealed class TraceCapture : System.Diagnostics.TraceListener
    {
        private readonly System.Text.StringBuilder _text = new();
        internal string Text { get { lock (_text) return _text.ToString(); } }
        public override void Write(string? message) { lock (_text) _text.Append(message); }
        public override void WriteLine(string? message) { lock (_text) _text.AppendLine(message); }
    }

    private sealed class RecordingLogger : ILogger
    {
        internal List<(int Id, string? Name, Type? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((eventId.Id, eventId.Name, exception?.GetType()));
    }

    private class BaseModel;
    private sealed class DerivedModel : BaseModel;
    private sealed class Attachment : IDisposable
    {
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
