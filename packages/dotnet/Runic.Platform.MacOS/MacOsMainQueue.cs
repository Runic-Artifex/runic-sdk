using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Platform.MacOS;

// The main queue: cleanup of owned panels and URLs, which must drain even after presentation replacement, and the
// application's own work without a window (MacApplicationOwner).
internal static partial class MacOsMainQueue
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    internal static unsafe ValueTask InvokeAsync(Action action)
    {
        if (IsMainThread() != 0) { action(); return ValueTask.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = GCHandle.Alloc((action, completion));
        Dispatch(GetMainQueue(), GCHandle.ToIntPtr(state), &Run);
        return new(completion.Task);
    }

    // Runs action on the main thread from the main run loop rather than the main queue, in every common mode, which
    // includes AppKit's modal panel mode. A modal loop (runModal) that a main-queue block started runs no other main-queue
    // block until it ends (CoreFoundation serves the main queue only outside its callouts), but it runs these: ending such
    // a loop, as cancellation does, goes through here. Called on the main thread, it runs at once.
    internal static ValueTask PerformAsync(Action action)
    {
        if (IsMainThread() != 0) { action(); return ValueTask.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return new(completion.Task);
    }

    // Queues action to the main run loop in every common mode, also from the main thread: it runs in the next loop
    // iteration, such as the first of a modal loop about to start. Exceptions are not reported.
    internal static void Post(Action action)
    {
        nint block = MacDesktopBlock.Create(action);
        try
        {
            // CFRunLoopPerformBlock copies the block; it is thread-safe, and the wakeup makes a waiting loop run it.
            nint main = GetMainRunLoop();
            PerformBlock(main, CommonModes.Value, block);
            WakeUp(main);
        }
        finally { MacDesktopBlock.Release(block); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Run(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var (action, completion) = ((Action, TaskCompletionSource))handle.Target!;
        handle.Free();
        try { action(); completion.SetResult(); }
        catch (Exception error) { completion.SetException(error); }
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_main_np")]
    private static partial int IsMainThread();
    // dispatch_get_main_queue is an inline accessor to this exported queue.
    private static readonly nint SystemLibrary = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
    private static nint GetMainQueue() => NativeLibrary.GetExport(SystemLibrary, "_dispatch_main_q");
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dispatch_async_f")]
    private static unsafe partial void Dispatch(nint queue, nint context, delegate* unmanaged[Cdecl]<nint, void> callback);

    // kCFRunLoopCommonModes, the CFString constant.
    private static readonly Lazy<nint> CommonModes = new(static () =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFRunLoopCommonModes")));
    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopGetMain")]
    private static partial nint GetMainRunLoop();
    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopPerformBlock")]
    private static partial void PerformBlock(nint runLoop, nint mode, nint block);
    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopWakeUp")]
    private static partial void WakeUp(nint runLoop);
}
