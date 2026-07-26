using System;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting.CsWebUi;

internal sealed class CsWebUiDispatcher : IUiDispatcher
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly AsyncLocal<int> AccessDepth = new();

    public bool CheckAccess() => AccessDepth.Value != 0;

    public async ValueTask InvokeAsync(
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (CheckAccess())
        {
            await callback(cancellationToken).ConfigureAwait(false);
            return;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AccessDepth.Value++;
        try
        {
            await callback(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AccessDepth.Value--;
            Gate.Release();
        }
    }

    public async ValueTask<TResult> InvokeAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (CheckAccess())
        {
            return await callback(cancellationToken).ConfigureAwait(false);
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AccessDepth.Value++;
        try
        {
            return await callback(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AccessDepth.Value--;
            Gate.Release();
        }
    }
}
