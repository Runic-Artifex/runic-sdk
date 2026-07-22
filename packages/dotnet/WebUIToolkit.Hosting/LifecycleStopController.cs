using System;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting;

internal sealed class LifecycleStopController : IApplicationStopController, IDisposable
{
    private readonly CancellationTokenSource _stoppingSource = new();
    private readonly Task _completion;
    private readonly Action _onStopWon;
    private readonly Action<StopReason>? _onStopSelected;
    private readonly TaskCompletionSource<StopReason> _requested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requestWon;

    internal LifecycleStopController(
        Task completion,
        Action onStopWon,
        Action<StopReason>? onStopSelected = null)
    {
        _completion = completion;
        _onStopWon = onStopWon;
        _onStopSelected = onStopSelected;
    }

    public CancellationToken Stopping => _stoppingSource.Token;

    public Task Completion => _completion;

    internal Task<StopReason> Requested => _requested.Task;

    public bool RequestStop(StopReason reason)
    {
        if (Interlocked.CompareExchange(ref _requestWon, 1, 0) != 0)
        {
            return false;
        }

        _onStopSelected?.Invoke(reason);
        _onStopWon();
        _requested.TrySetResult(reason);
        try
        {
            _stoppingSource.Cancel();
        }
        catch (AggregateException)
        {
            // Cancellation callbacks are consumer-owned. A callback failure must not
            // prevent the deterministic stop signal from reaching the kernel.
        }

        return true;
    }

    public void Dispose()
    {
        _stoppingSource.Dispose();
    }
}
