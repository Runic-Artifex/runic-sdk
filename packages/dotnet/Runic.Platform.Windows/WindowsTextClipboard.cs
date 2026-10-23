using System.Collections.Immutable;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// ResourceBusy means the clipboard could not be opened and nothing changed.
internal interface IWindowsClipboard
{
    PlatformResult<string?> Read(nint owner, int maximumCharacters);
    PlatformResult<PlatformUnit> Write(nint owner, string text);
}

// The formats beyond text, with the same ResourceBusy rule. Formats needs no open clipboard and never reports busy.
internal interface IWindowsClipboardData
{
    PlatformResult<DesktopClipboardFormats> Formats();
    PlatformResult<string?> ReadHtml(nint owner, int maximumCharacters);
    PlatformResult<string?> ReadRtf(nint owner, int maximumCharacters);
    PlatformResult<byte[]?> ReadImage(nint owner, int maximumBytes);
    PlatformResult<ImmutableArray<string>> ReadFiles(nint owner, int maximumCount);
    PlatformResult<PlatformUnit> Write(nint owner, DesktopClipboardContent content);
    // Signals each clipboard change, from any thread, until disposed; null when the listener could not start.
    ValueTask<IAsyncDisposable?> ListenAsync(Action changed);
}

// Every operation runs on the window's thread with its HWND as the clipboard owner, and an operation that found the
// clipboard busy is retried a bounded number of times. Without data, the formats beyond text report BackendUnavailable.
internal sealed class WindowsTextClipboard(INativeWindowOwner owner, IWindowsClipboard native, IWindowsClipboardData? data = null) : IDesktopClipboard
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

    public ValueTask<PlatformResult<DesktopClipboardFormats>> GetFormatsAsync(CancellationToken cancellationToken = default) =>
        DataAsync(static (data, _) => data.Formats(), cancellationToken);

    public ValueTask<PlatformResult<string?>> ReadHtmlAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        return DataAsync((data, handle) => data.ReadHtml(handle, maximumCharacters), cancellationToken);
    }

    public ValueTask<PlatformResult<string?>> ReadRtfAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        return DataAsync((data, handle) => data.ReadRtf(handle, maximumCharacters), cancellationToken);
    }

    public ValueTask<PlatformResult<byte[]?>> ReadImageAsync(int maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        return DataAsync((data, handle) => data.ReadImage(handle, maximumBytes), cancellationToken);
    }

    public ValueTask<PlatformResult<ImmutableArray<string>>> ReadFilesAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        return DataAsync((data, handle) => data.ReadFiles(handle, maximumCount), cancellationToken);
    }

    public ValueTask<PlatformResult<PlatformUnit>> WriteAsync(DesktopClipboardContent content, CancellationToken cancellationToken = default)
    {
        DesktopClipboardRules.Check(content);
        if (DesktopClipboardRules.Validate(content) is { } invalid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Failed(invalid));
        }
        return DataAsync((data, handle) => data.Write(handle, content), cancellationToken);
    }

    public IAsyncEnumerable<PlatformResult<DesktopClipboardFormats>> WatchAsync(CancellationToken cancellationToken = default) =>
        ClipboardWatch.WatchAsync(GetFormatsAsync,
            (changed, _) => data is null || !owner.IsAvailable ? ValueTask.FromResult<IAsyncDisposable?>(null) : data.ListenAsync(changed),
            cancellationToken);

    private ValueTask<PlatformResult<T>> DataAsync<T>(Func<IWindowsClipboardData, nint, PlatformResult<T>> action, CancellationToken cancellationToken)
    {
        if (data is not { } formats)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<T>>(new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        }
        return InvokeAsync(handle => action(formats, handle), cancellationToken);
    }

    // Internal for the native tests, which open the clipboard themselves through the same retry.
    internal async ValueTask<PlatformResult<T>> InvokeAsync<T>(Func<nint, PlatformResult<T>> action, CancellationToken cancellationToken)
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
