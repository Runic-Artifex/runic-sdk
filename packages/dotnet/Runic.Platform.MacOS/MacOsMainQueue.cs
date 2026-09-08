using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Platform.MacOS;

// Cleanup only: owned panels/URLs must drain even after presentation replacement.
// This does not provide a way to start operations without a verified owner.
internal static partial class MacOsMainQueue
{
    internal static unsafe ValueTask InvokeAsync(Action action)
    {
        if (IsMainThread() != 0) { action(); return ValueTask.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = GCHandle.Alloc((action, completion));
        Dispatch(GetMainQueue(), GCHandle.ToIntPtr(state), &Run);
        return new(completion.Task);
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
}
