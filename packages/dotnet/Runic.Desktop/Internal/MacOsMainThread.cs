using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// The macOS process main thread. Managed work for AppKit goes through the main dispatch queue, as
// Runic.Platform.MacOS's MacOsMainQueue does, so both packages share one queue: the run loop drains it in its common
// modes, which include the event-tracking and modal-panel modes of menus, live resize and modal panels. It does not
// drain it inside another block of the queue, so work that tracks a menu or runs a modal loop starts outside it
// (MacOsWkWebViewHost.ShowContextMenu).
internal static unsafe partial class MacOsMainThread
{
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";

    internal static bool IsMainThread => OperatingSystem.IsMacOS() && PthreadMainNp() != 0;

    // Runs the action now on the main thread, else queues it there. Cancellation claims queued work only.
    internal static ValueTask InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsMainThread)
        {
            action();
            return ValueTask.CompletedTask;
        }
        var work = new NativeDispatchWork(action, cancellationToken);
        Queue(work);
        return new ValueTask(work.WaitAsync());
    }

    // Queues the action on the main thread without waiting for it. The action reports its own failures.
    internal static void Post(Action action) => Queue(new NativeDispatchWork(action, CancellationToken.None));

    // Queues the action on the main thread after the delay, without waiting for it.
    internal static void PostAfter(TimeSpan delay, Action action)
    {
        var handle = GCHandle.Alloc(new NativeDispatchWork(action, CancellationToken.None));
        DispatchAfter(DispatchTime(0, (long)delay.TotalNanoseconds), MainQueue.Value, GCHandle.ToIntPtr(handle), &Run);
    }

    private static void Queue(NativeDispatchWork work)
    {
        var handle = GCHandle.Alloc(work);
        DispatchAsync(MainQueue.Value, GCHandle.ToIntPtr(handle), &Run);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Run(nint context)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(context);
            var work = (NativeDispatchWork)handle.Target!;
            handle.Free();
            work.Run();
        }
        catch
        {
            // Never unwind through libdispatch.
        }
    }

    // dispatch_get_main_queue is an inline accessor to this exported queue.
    private static readonly Lazy<nint> MainQueue = new(static () =>
        NativeLibrary.GetExport(NativeLibrary.Load(SystemLibrary), "_dispatch_main_q"));

    [LibraryImport(SystemLibrary, EntryPoint = "pthread_main_np")]
    private static partial int PthreadMainNp();

    [LibraryImport(SystemLibrary, EntryPoint = "dispatch_async_f")]
    private static partial void DispatchAsync(nint queue, nint context, delegate* unmanaged[Cdecl]<nint, void> work);

    [LibraryImport(SystemLibrary, EntryPoint = "dispatch_after_f")]
    private static partial void DispatchAfter(ulong when, nint queue, nint context, delegate* unmanaged[Cdecl]<nint, void> work);

    // DISPATCH_TIME_NOW is 0.
    [LibraryImport(SystemLibrary, EntryPoint = "dispatch_time")]
    private static partial ulong DispatchTime(ulong when, long delta);
}
