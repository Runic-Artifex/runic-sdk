using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
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
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Program.Check(lifecycle.State == RunicViewHostState.Opening, "The open did not report Opening.");
        var second = lifecycle.RequestAsync(() => true, (owner, _) =>
        {
            log.Add("second opened");
            owner.Own(() => log.Add("second released"));
            return Task.CompletedTask;
        });
        await second.WaitAsync(TimeSpan.FromSeconds(5));
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
        await last.WaitAsync(TimeSpan.FromSeconds(5));
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
        var view = await services.CreateWpfViewForModelAsync(host, Program.HtmlOptions(), model, null, CancellationToken.None);
        Program.Check(ReferenceEquals(view.ViewModel, model) && view is WpfBridgeView<BaseModel>, "The base-class Bridge was not used.");
        await view.DisposeAsync();
        Program.Check(attachment.Disposed, "The session did not release its attachment.");
        try
        {
            await new ServiceCollection().BuildServiceProvider()
                .CreateWpfViewForModelAsync(host, Program.HtmlOptions(), model, null, CancellationToken.None);
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
