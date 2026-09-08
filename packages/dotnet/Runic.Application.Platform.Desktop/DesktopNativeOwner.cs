using System;
using System.Threading;
using System.Threading.Tasks;
using Runic.Desktop;
using Runic.Platform.Runtime;

namespace Runic.Application.Platform.Desktop;

/// <summary>Binds native work to one C# Desktop window identity for one presentation.</summary>
/// <remarks>A reconnect retains its window. A replacement cannot inherit this owner's work.</remarks>
public sealed class DesktopNativeOwner : INativePickerOwner
{
    private readonly Func<DesktopWindow?> _window;
    private DesktopWindow? _identity;

    /// <summary>Creates an owner that binds when its embedded window becomes available.</summary>
    public DesktopNativeOwner(Func<DesktopWindow?> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
    }

    /// <inheritdoc />
    public Guid Generation { get; } = Guid.NewGuid();

    /// <inheritdoc />
    public bool IsAvailable => Current() is not null;

    /// <summary>Reports access to this verified native owner's thread.</summary>
    public bool CheckAccess() => Current()?.CheckNativeAccess() == true;

    private DesktopWindow? Current()
    {
        var current = _window();
        if (current is not { SupportsNativeDispatch: true }) return null;
        var identity = Interlocked.CompareExchange(ref _identity, current, null) ?? current;
        return ReferenceEquals(current, identity) ? current : null;
    }

    /// <inheritdoc />
    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        var current = Current() ?? throw new OwnerClosedException();
        return DispatchVerifiedAsync(current.DispatchNativeAsync,
            () => ReferenceEquals(Current(), current), action, cancellationToken);
    }

    internal static async ValueTask DispatchVerifiedAsync(
        Func<Action<nint>, CancellationToken, ValueTask> dispatch,
        Func<bool> isCurrent, Action<nint> action, CancellationToken cancellationToken)
    {
        int callbackStarted = 0;
        try
        {
            await dispatch(handle =>
            {
                // Verify again after queueing, before the handle is used by native code.
                if (!isCurrent()) throw new OwnerClosedException();
                Volatile.Write(ref callbackStarted, 1);
                action(handle);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (Volatile.Read(ref callbackStarted) == 0
            && !cancellationToken.IsCancellationRequested
            && error is ObjectDisposedException or NotSupportedException or OperationCanceledException
            && !isCurrent())
        {
            throw new OwnerClosedException();
        }
    }
}
