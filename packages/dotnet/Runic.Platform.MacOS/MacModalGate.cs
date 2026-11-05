namespace Runic.Platform.MacOS;

// Application-modal UI without a window, one at a time, process-wide: unowned file panels that run modally (runModal,
// without an AppKit event loop) and unowned alerts. A modal loop runs inside a main-queue block and runs no other
// main-queue block while it lasts; one started right after another closed would keep the first request's cleanup, which
// the main queue runs, from finishing until it closed in turn, and AppKit's modal sessions end only in LIFO order. A later
// request therefore waits here, off the main thread, until the earlier one has completed, cleanup included; cancelling a
// waiting request completes it at once.
internal static class MacModalGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Throws OperationCanceledException when cancellationToken is cancelled while waiting.
    internal static Task EnterAsync(CancellationToken cancellationToken) => Gate.WaitAsync(cancellationToken);

    internal static void Exit() => Gate.Release();
}
