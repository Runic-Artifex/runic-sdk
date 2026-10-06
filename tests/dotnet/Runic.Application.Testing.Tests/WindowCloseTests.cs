using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// Graceful window close drains accepted work before cancelling it, including
// commands invoked through their ordinary awaited route.
public static class WindowCloseTests
{
    public static async Task RunAsync()
    {
        await CloseWaitsForAnAwaitedCommand();
        await CloseTimeoutCancelsRemainingAwaitedWork();
        await AdmittedOperationIsNotCancelledBeforeTheTimeout();
    }

    private static async Task CloseWaitsForAnAwaitedCommand()
    {
        var model = new SlowModel();
        using var transport = new InMemoryViewTransport();
        var session = new WindowContentSession(transport, rootModel: model);
        using var bridge = new SlowBridge(transport, model, session);
        var call = transport.CallAsync("slowSave").AsTask();
        Require(await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)), "The awaited command did not start.");

        var close = session.BeginCloseAsync(TimeSpan.FromSeconds(10)).AsTask();
        await Task.Delay(50);
        Require(!close.IsCompleted && !model.Cancelled,
            "Close did not wait for an awaited command, or cancelled it before its timeout.");
        using (var rejected = JsonDocument.Parse(await transport.CallAsync("slowSave")))
            Require(rejected.RootElement.GetProperty("error").GetProperty("kind").GetString() == "disconnected",
                "A closing window admitted a new awaited command.");

        model.Release.TrySetResult();
        using (var reply = JsonDocument.Parse(await call))
            Require(reply.RootElement.GetProperty("ok").GetBoolean(), "The drained awaited command did not succeed.");
        var result = await close;
        Require(result.Drained && result.RemainingOperations == 0, "Close did not report the drained awaited command.");
        Require(result.Completion.IsCompletedSuccessfully, "A drained close left its completion pending.");
        session.Dispose();
    }

    private static async Task CloseTimeoutCancelsRemainingAwaitedWork()
    {
        var model = new SlowModel();
        using var transport = new InMemoryViewTransport();
        var session = new WindowContentSession(transport, rootModel: model);
        using var bridge = new SlowBridge(transport, model, session);
        var call = transport.CallAsync("slowSave").AsTask();
        Require(await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)), "The awaited command did not start.");

        var timedOut = await session.BeginCloseAsync(TimeSpan.Zero);
        Require(!timedOut.Drained && timedOut.RemainingOperations == 1,
            "Close did not count a running awaited command.");
        using (var reply = JsonDocument.Parse(await call.WaitAsync(TimeSpan.FromSeconds(5))))
            Require(reply.RootElement.GetProperty("error").GetProperty("kind").GetString() == "cancelled" && model.Cancelled,
                "The close timeout did not cancel the remaining awaited command.");
        await timedOut.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var drained = await session.BeginCloseAsync(Timeout.InfiniteTimeSpan);
        Require(drained.Drained, "Close did not drain after cancellation.");
        session.Dispose();
    }

    private static async Task AdmittedOperationIsNotCancelledBeforeTheTimeout()
    {
        using var registry = new BridgeOperationRegistry("close-drain-test");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = CancellationToken.None;
        _ = registry.Accept("drained", async cancellation =>
        {
            token = cancellation;
            await release.Task;
        });
        var close = registry.BeginCloseAsync(TimeSpan.FromSeconds(10)).AsTask();
        await Task.Delay(50);
        Require(!close.IsCompleted && !token.IsCancellationRequested,
            "Close cancelled accepted work before waiting for it.");
        release.SetResult();
        Require((await close).Drained, "Close did not report drained work.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SlowModel : INotifyPropertyChanged
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public ICommand Save { get; } = new NoopCommand();

        public async Task SaveAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private sealed class SlowBridge(IBridgeTransport transport, SlowModel model, WindowContentSession content)
        : ViewModelBridge<SlowModel>(transport, model, "slow", static (writer, _, revision) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteEndObject();
        }, [],
        [new("Save", vm => vm.Save, ExecuteAsync: (vm, cancellation, _) => vm.SaveAsync(cancellation),
            CanExecute: (_, _) => true)],
        content: content);

    private sealed class NoopCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
