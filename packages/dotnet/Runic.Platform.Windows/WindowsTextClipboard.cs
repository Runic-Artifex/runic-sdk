using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

internal interface IWindowsClipboard
{
    PlatformResult<string?> Read(nint owner, int maximumCharacters);
    PlatformResult<Unit> Write(nint owner, string text);
}

internal sealed class WindowsTextClipboard(INativePickerOwner owner, IWindowsClipboard native) : ITextClipboard
{
    public ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        return InvokeAsync(handle => native.Read(handle, maximumCharacters), cancellationToken);
    }

    public ValueTask<PlatformResult<Unit>> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0')) return ValueTask.FromResult<PlatformResult<Unit>>(new PlatformResult<Unit>.Failed(FailureCode.InvalidData));
        return InvokeAsync(handle => native.Write(handle, text), cancellationToken);
    }

    private async ValueTask<PlatformResult<T>> InvokeAsync<T>(Func<nint, PlatformResult<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsAvailable) return new PlatformResult<T>.Unavailable(UnavailableReason.OwnerUnavailable);
        var generation = owner.Generation;
        PlatformResult<T> result = new PlatformResult<T>.Unavailable(UnavailableReason.OwnerUnavailable);
        try
        {
            await owner.InvokeAsync(handle =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (handle == 0 || !owner.IsAvailable || owner.Generation != generation) return;
                // Once native execution begins it must finish and report the actual outcome.
                result = action(handle);
            }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OwnerClosedException) { return new PlatformResult<T>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (ObjectDisposedException) { return new PlatformResult<T>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (DllNotFoundException) { return new PlatformResult<T>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (EntryPointNotFoundException) { return new PlatformResult<T>.Unavailable(UnavailableReason.BackendUnavailable); }
    }
}
