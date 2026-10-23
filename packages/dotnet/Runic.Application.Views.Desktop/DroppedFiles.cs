using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

// A native drop as the root ViewModel receives it (RunicFileDrop), and the promised files it leaves behind. Shared by the
// Desktop windows and the WPF child view.
internal static class DroppedFiles
{
    internal static RunicFileDrop ToRunicDrop(DesktopFileDropEventArgs drop) =>
        new(drop.Paths.Select(RunicDroppedFile.FromPath).ToArray(), drop.Target)
        {
            Effect = drop.Effect switch
            {
                DesktopDragEffects.Move => RunicDropEffect.Move,
                DesktopDragEffects.Link => RunicDropEffect.Link,
                _ => RunicDropEffect.Copy,
            },
            PromisedFiles = drop.PromisedFiles.Select(static lease => (RunicPromisedFile)new LeasedPromisedFile(lease)).ToArray(),
        };

    // Delivers the drop, then disposes the promised files the handler did not open; an opened one is the handler's.
    internal static async Task DeliverAsync(WindowContentSession content, RunicFileDrop drop, CancellationToken cancellationToken)
    {
        try
        {
            await content.DeliverFileDropAsync(drop, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseUnopenedAsync(drop).ConfigureAwait(false);
        }
    }

    // For a drop that is never delivered, and after delivery: deleting a staged copy cannot fail the window.
    internal static async Task ReleaseUnopenedAsync(RunicFileDrop drop)
    {
        foreach (var file in drop.PromisedFiles)
        {
            if (file is LeasedPromisedFile { IsOpened: false } unopened)
            {
                try { await unopened.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
        }
    }

    private sealed class LeasedPromisedFile(IReadFileLease lease) : RunicPromisedFile
    {
        private int _opened;

        internal bool IsOpened => Volatile.Read(ref _opened) != 0;

        public override string Name { get; } = SafeName(lease.DisplayName);

        public override ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Exchange(ref _opened, 1);
            return lease.OpenReadAsync(cancellationToken);
        }

        public override ValueTask DisposeAsync() => lease.DisposeAsync();

        private static string SafeName(string? name)
        {
            var file = Path.GetFileName(name?.Replace('\\', '/') ?? string.Empty);
            return string.IsNullOrWhiteSpace(file) || file is "." or ".." ? "file" : file;
        }
    }
}
