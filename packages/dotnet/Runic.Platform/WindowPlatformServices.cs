using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>Native services owned by one application window.</summary>
/// <remarks>
/// A host registers one instance per window scope, so a ViewModel can take this interface, or
/// <see cref="IFileDialogs"/>, <see cref="ITextClipboard"/> or <see cref="IDesktopFileLauncher"/> directly, in its
/// constructor. The services follow the window: before it opens they report
/// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>, and once it closes
/// <see cref="PlatformUnavailableReason.OwnerClosed"/>. A host without a native owner reports every service
/// unavailable instead of failing to resolve it.
/// </remarks>
public interface IWindowPlatformServices : IPlatformCapabilities
{
    /// <summary>File and directory dialogs owned by the window.</summary>
    IFileDialogs FileDialogs { get; }
    /// <summary>The Unicode text clipboard, used through the window's native thread.</summary>
    ITextClipboard Clipboard { get; }
    /// <summary>Hands local files to native applications on behalf of the window.</summary>
    IDesktopFileLauncher FileLauncher { get; }
}

/// <summary>Window platform services for hosts that cannot provide them.</summary>
public static class WindowPlatformServices
{
    /// <summary>The capability identifiers a window's platform services report.</summary>
    public static ImmutableArray<string> CapabilityIds { get; } =
    [
        "platform.files.open", "platform.files.save", "platform.directories.open", "platform.dialogs.owned",
        "platform.clipboard.readText", "platform.clipboard.writeText", "platform.files.launch",
    ];

    /// <summary>Creates services that report every operation and capability unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>
    /// Dialogs return <see cref="PickerResult{T}.Unavailable"/> and the clipboard and launcher
    /// <see cref="PlatformResult{T}.Unavailable"/>; nothing throws except for invalid arguments and caller cancellation.
    /// </remarks>
    public static IWindowPlatformServices Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableWindowPlatformServices(reason);
    }

    private sealed class UnavailableWindowPlatformServices(PlatformUnavailableReason reason)
        : IWindowPlatformServices, IFileDialogs, ITextClipboard, IDesktopFileLauncher
    {
        private readonly CapabilitySnapshot _snapshot = new(Guid.Empty,
            CapabilityIds.ToImmutableDictionary(static id => id, CapabilityStatus (_) => new CapabilityStatus.Unavailable(reason)));

        public IFileDialogs FileDialogs => this;
        public ITextClipboard Clipboard => this;
        public IDesktopFileLauncher FileLauncher => this;
        public CapabilitySnapshot GetSnapshot() => _snapshot;

        public ValueTask<PickerResult<IDirectoryLease>> OpenDirectoryAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Pick<IDirectoryLease>(cancellationToken);
        }

        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Pick<IReadFileLease>(cancellationToken);
        }

        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Pick<ISaveFileLease>(cancellationToken);
        }

        public ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
            return Result<string?>(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(text);
            return Result<PlatformUnit>(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
            return Result<PlatformUnit>(cancellationToken);
        }

        private ValueTask<PickerResult<T>> Pick<T>(CancellationToken cancellationToken) where T : IAsyncDisposable
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PickerResult<T>>(new PickerResult<T>.Unavailable(reason));
        }

        private ValueTask<PlatformResult<T>> Result<T>(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<T>>(new PlatformResult<T>.Unavailable(reason));
        }
    }
}
