using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// The application's GTK thread without a window: the thread that runs GLib's default main context, which GTK 3's
// gtk_main and GTK 4's application loop both iterate. Work is queued with g_idle_add, so it never runs on the caller's
// thread unless that is the GTK thread. While no loop runs (no toolkit started, or GTK 3 before its first window), the
// default context has no owner and work fails with NativeBackendUnavailableException instead of waiting forever.
// Actions receive no window (0). It lives here, beside the portal, so a GTK 4 process uses it without loading the GTK 3
// provider.
internal sealed unsafe partial class GLibApplicationOwner : INativeWindowOwner
{
    private const string GLib = "libglib-2.0.so.0";

    internal static GLibApplicationOwner Shared { get; } = new();

    public Guid Generation { get; } = Guid.NewGuid();

    // GLib lives as long as the process; whether a loop runs is checked per invocation.
    public bool IsAvailable => true;

    // Whether a thread runs the default main context now.
    internal static bool IsRunning
    {
        get
        {
            try
            {
                if (IsOwner(0) != 0) return true;
                if (Acquire(0) == 0) return true;
                Release(0);
                return false;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }

    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // On the GTK thread itself: at once.
            if (IsOwner(0) != 0)
            {
                action(0);
                return ValueTask.CompletedTask;
            }
            // Nobody iterates the default context: queued work would never run.
            if (Acquire(0) != 0)
            {
                Release(0);
                return ValueTask.FromException(new NativeBackendUnavailableException(
                    "No GTK main loop runs: open a Desktop window first, or run DesktopEventLoop.Run with WithGtk4()."));
            }
            var work = new Work(action, cancellationToken);
            var handle = GCHandle.Alloc(work);
            if (IdleAdd(&Run, GCHandle.ToIntPtr(handle)) == 0)
            {
                handle.Free();
                return ValueTask.FromException(new NativeBackendUnavailableException("GLib did not queue the work."));
            }
            return new(work.Completion.Task);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return ValueTask.FromException(new NativeBackendUnavailableException("GLib is not available.", error));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Run(nint data)
    {
        var handle = GCHandle.FromIntPtr(data);
        var work = (Work)handle.Target!;
        handle.Free();
        work.Execute();
        return 0; // G_SOURCE_REMOVE
    }

    private sealed class Work(Action<nint> action, CancellationToken cancellationToken)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(cancellationToken);
                return;
            }
            try
            {
                action(0);
                Completion.TrySetResult();
            }
            catch (Exception error) { Completion.TrySetException(error); }
        }
    }

    [LibraryImport(GLib, EntryPoint = "g_main_context_is_owner")] private static partial int IsOwner(nint context);
    [LibraryImport(GLib, EntryPoint = "g_main_context_acquire")] private static partial int Acquire(nint context);
    [LibraryImport(GLib, EntryPoint = "g_main_context_release")] private static partial void Release(nint context);
    [LibraryImport(GLib, EntryPoint = "g_idle_add")] private static partial uint IdleAdd(delegate* unmanaged[Cdecl]<nint, int> function, nint data);
}
