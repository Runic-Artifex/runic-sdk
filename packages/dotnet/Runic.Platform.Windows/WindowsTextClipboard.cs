using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// ResourceBusy means the clipboard could not be opened and nothing changed.
internal interface IWindowsClipboard
{
    PlatformResult<string?> Read(nint owner, int maximumCharacters);
    PlatformResult<PlatformUnit> Write(nint owner, string text);
}

internal sealed class WindowsTextClipboard(INativePickerOwner owner, IWindowsClipboard native) : ITextClipboard
{
    internal const int BusyAttempts = 10;

    public ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        return InvokeAsync(handle => native.Read(handle, maximumCharacters), cancellationToken);
    }

    public ValueTask<PlatformResult<PlatformUnit>> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0')) return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData));
        return InvokeAsync(handle => native.Write(handle, text), cancellationToken);
    }

    private async ValueTask<PlatformResult<T>> InvokeAsync<T>(Func<nint, PlatformResult<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsAvailable) return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        var generation = owner.Generation;
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                PlatformResult<T> result = new PlatformResult<T>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
                await owner.InvokeAsync(handle =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (handle == 0 || !owner.IsAvailable || owner.Generation != generation) return;
                    // Once native execution begins it must finish and report the actual outcome.
                    result = action(handle);
                }, cancellationToken).ConfigureAwait(false);
                // Another process may hold the clipboard briefly. A busy result changed
                // nothing, so wait off the UI thread and retry a bounded number of times.
                if (result is not PlatformResult<T>.Failed { Code: PlatformFailureCode.ResourceBusy } || attempt == BusyAttempts) return result;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10 << attempt, 100)), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OwnerClosedException) { return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (ObjectDisposedException) { return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (DllNotFoundException) { return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
    }
}
