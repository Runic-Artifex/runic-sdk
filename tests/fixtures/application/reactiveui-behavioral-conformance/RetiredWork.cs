// I-192: a ReactiveUI operation's call replies `cancelled` as soon as its cancellation is requested, while a token-taking
// command's body can still run. When a retiring navigation entry cancels an operation declared Cancel (or waits for one
// declared Drain), the page is released only after the execution that the call started ended: the registry tracks it
// as the operation's work. A cancelled CreateFromObservable execution ends with its unsubscription.
using Runic.Application.Views;
using Runic.Application.Views.Generated;
using Runic.Navigation;
using TUnit.Core;

#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using Runic.Application.Views.ReactiveUI.Reactive;
using Runic.Application.Views.ReactiveUI.Reactive.Generated;
using FlavorUnit = System.Reactive.Unit;
#else
using ReactiveUI;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views.ReactiveUI.Generated;
using FlavorUnit = ReactiveUI.Primitives.RxVoid;
#endif

internal sealed class RetiredWorkTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test, NotInParallel(Order = 27)]
    public async Task RetirementWaitsForTheExecutionToEndAsync()
    {
        var handled = DefaultExceptionHandlerRecorder.Install();
        handled.Clear();
        await using var context = new RunicModelContext();
        var scheduler = ReactiveSchedulers.For(context);
        using var registry = new BridgeOperationRegistry("conformance");
        var page = new object();

        // A body that keeps running after its cancellation until the test releases it.
        var lingering = new Lingering();
        var command = ReactiveCommand.CreateFromTask<FlavorUnit, int>(lingering.RunAsync, scheduler);
        var upload = Start(registry, page, "Upload", command);
        await lingering.Started.Task.WaitAsync(Timeout);
        var retired = registry.Retire(page);
        var status = await registry.WaitForTerminalAsync(upload).AsTask().WaitAsync(Timeout);
        Require(status.Kind is BridgeOperationStatusKind.Cancelled, $"Retirement did not cancel the operation: {status.Kind}.");
        await TurnsAsync(context);
        Require(!retired.IsCompleted && lingering.Cancelled.Task.IsCompleted,
            "Retirement ended before the cancelled command's body did.");
        lingering.Release.TrySetResult();
        await retired.WaitAsync(Timeout);
        Require(lingering.Ended, "Retirement ended before the cancelled command's body did.");

        // CreateFromObservable: the unsubscription ends the execution, without a notification.
        var never = new NeverObservable<int>();
        var observable = ReactiveCommand.CreateFromObservable<FlavorUnit, int>(_ => never, scheduler);
        var watch = Start(registry, page, "Watch", observable);
        var retiredWatch = registry.Retire(page);
        var watchStatus = await registry.WaitForTerminalAsync(watch).AsTask().WaitAsync(Timeout);
        Require(watchStatus.Kind is BridgeOperationStatusKind.Cancelled, $"Retirement did not cancel the observable operation: {watchStatus.Kind}.");
        await retiredWatch.WaitAsync(Timeout);
        Require(never.Disposals == 1, "Retirement ended before the observable execution was unsubscribed.");

        await TurnsAsync(context);
        Require(handled.IsEmpty, $"A retired execution's cancellation reached the default handler: {string.Join(", ", handled)}");
    }

    // Starts an operation declared Cancel that page owns, as a generated Bridge does.
    private static string Start(BridgeOperationRegistry registry, object page, string member,
        global::ReactiveUI.IReactiveCommand<FlavorUnit, int> command)
    {
        var request = new BridgeOperationRequest("conformance", member, $"{member}-1", BridgeOperationRequest.CanonicalDigest("null"));
        var admission = registry.AcceptUnlessRefused(request, () => null, async cancellation =>
        {
            await ReactiveCommandExecution.ExecuteCompletion(command, FlavorUnit.Default, cancellation).ConfigureAwait(false);
            return BridgeOperationResult.None;
        }, new BridgeOperationRetirement(RunicOperationRetirement.Cancel, page, [], "page"));
        Require(admission.Kind is BridgeOperationAdmissionKind.Accepted, $"The operation was not admitted: {admission.Reason}.");
        return admission.RequestId;
    }

    private static async Task TurnsAsync(RunicModelContext context)
    {
        for (var turn = 0; turn < 3; turn++)
        {
            await Task.Delay(20);
            await context.InvokeAsync(() => { });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Lingering
    {
        private int _ended;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Ended => Volatile.Read(ref _ended) == 1;

        public async Task<int> RunAsync(FlavorUnit _, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                await Release.Task.ConfigureAwait(false);
                throw;
            }
            finally { Volatile.Write(ref _ended, 1); }
        }
    }
}
