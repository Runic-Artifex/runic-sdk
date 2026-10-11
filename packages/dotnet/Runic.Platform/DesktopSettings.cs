using System.Runtime.CompilerServices;

namespace Runic.Platform;

/// <summary>Desktop preferences for hosts and platforms that cannot provide them.</summary>
public static class DesktopSettings
{
    /// <summary>Creates preferences that report every read unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>
    /// <see cref="IDesktopSettings.ReadAsync"/> returns <see cref="PlatformResult{T}.Unavailable"/>, and
    /// <see cref="IDesktopSettings.WatchAsync"/> emits that value once and then waits for cancellation or disposal,
    /// as a watch with no further changes does. Nothing throws except for caller cancellation and use after disposal.
    /// </remarks>
    public static IDesktopSettings Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableDesktopSettings(reason);
    }

    private sealed class UnavailableDesktopSettings(PlatformUnavailableReason reason) : IDesktopSettings
    {
        private readonly CancellationTokenSource _disposed = new();
        private readonly PlatformResult<DesktopAppearance> _result = new PlatformResult<DesktopAppearance>.Unavailable(reason);

        public ValueTask<PlatformResult<DesktopAppearance>> ReadAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_result);
        }

        public async IAsyncEnumerable<PlatformResult<DesktopAppearance>> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
            linked.Token.ThrowIfCancellationRequested();
            yield return _result;
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            _disposed.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
