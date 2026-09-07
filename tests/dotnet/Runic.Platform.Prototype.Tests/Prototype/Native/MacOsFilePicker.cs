using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Platform.Prototype;

internal sealed partial class MacOsFilePicker(DesktopPickerOwner owner) : INativeFilePicker
{
    internal TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<NativeFileSelection?> SelectAsync(bool save, string? suggestedName, CancellationToken cancellationToken)
    {
        nint panel = 0;
        var result = new TaskCompletionSource<NativeFileSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        Task cancellation = Task.CompletedTask;
        try
        {
            await OnOwnerAsync(owner, window =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                panel = Send(GetClass(save ? "NSSavePanel" : "NSOpenPanel"), Sel(save ? "savePanel" : "openPanel"));
                if (panel == 0) throw new IOException("AppKit could not create a file panel.");
                Send(panel, Sel("retain"));
                if (save)
                {
                    nint name = String(suggestedName!);
                    try { SendArg(panel, Sel("setNameFieldStringValue:"), name); }
                    finally { Send(name, Sel("release")); }
                }
                else
                {
                    SendArg(panel, Sel("setCanChooseDirectories:"), 0);
                    SendArg(panel, Sel("setAllowsMultipleSelection:"), 0);
                }
                nint block = CompletionBlock.Create(response =>
                {
                    using var pool = new AutoreleasePool();
                    try
                    {
                        if (response != 1) { result.TrySetResult(null); return; }
                        nint url = Send(panel, Sel("URL"));
                        if (url == 0) throw new IOException("AppKit returned no selected URL.");
                        var access = MacOsSecurityAccess.Acquire(url, owner);
                        try
                        {
                            string path = Marshal.PtrToStringUTF8(Send(Send(url, Sel("path")), Sel("UTF8String")))
                                ?? throw new IOException("AppKit returned no local path.");
                            // A sandbox's user-selected grant authorizes the selected URL,
                            // not arbitrary sibling staging files. Never invent that access.
                            bool sandboxed = IsSandboxed();
                            result.TrySetResult(new(path, access, !sandboxed && !access.Started));
                        }
                        catch { access.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
                    }
                    catch (Exception error) { result.TrySetException(error); }
                });
                try { BeginSheet(panel, Sel("beginSheetModalForWindow:completionHandler:"), window, block); }
                finally { CompletionBlock.Release(block); }
                Shown.TrySetResult();
            }, cancellationToken).ConfigureAwait(false);
            registration = cancellationToken.Register(() => cancellation = CancelAsync());
            async Task CancelAsync()
            {
                try
                {
                    await OnOwnerAsync(owner, _ =>
                    {
                        if (!result.Task.IsCompleted) SendArg(panel, Sel("cancel:"), 0);
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) { result.TrySetException(error); }
            }
            return await result.Task.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            await cancellation.ConfigureAwait(false);
            if (panel != 0) await OnOwnerAsync(owner, _ => Send(panel, Sel("release")), CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Retain the actual NSURL. Balance only the access explicitly started here;
    // false can also mean an ordinary non-scoped URL. Actual file IO checks access.
    internal sealed class MacOsSecurityAccess(nint url, bool started, DesktopPickerOwner owner) : IAsyncDisposable
    {
        private TaskCompletionSource? _closed;
        internal bool Started => started;
        internal static MacOsSecurityAccess Acquire(nint url, DesktopPickerOwner owner)
        {
            Send(url, Sel("retain"));
            return new(url, SendBool(url, Sel("startAccessingSecurityScopedResource")) != 0, owner);
        }
        public ValueTask DisposeAsync()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _closed, completion, null) is { } existing) return new(existing.Task);
            _ = ReleaseAsync(completion);
            return new(completion.Task);
        }
        private async Task ReleaseAsync(TaskCompletionSource completion)
        {
            try
            {
                await OnOwnerAsync(owner, _ =>
                {
                    if (started) Send(url, Sel("stopAccessingSecurityScopedResource"));
                    Send(url, Sel("release"));
                }).ConfigureAwait(false);
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }
    }

    // Real Objective-C block copy/dispose ownership. AppKit copies the block;
    // each heap copy owns a GCHandle and releases it when AppKit releases it.
    private static unsafe partial class CompletionBlock
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Block { internal nint Isa; internal int Flags, Reserved; internal nint Invoke, Descriptor, Context; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Descriptor { internal nuint Reserved, Size; internal nint Copy, Dispose, Signature; }
        private static readonly nint SystemLibrary = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        private static readonly nint StackBlock = NativeLibrary.GetExport(SystemLibrary, "_NSConcreteStackBlock");
        // Process-lifetime ABI descriptor; no per-dialog native allocation retained.
        private static readonly Descriptor* Description = CreateDescriptor();
        private static Descriptor* CreateDescriptor()
        {
            var descriptor = (Descriptor*)NativeMemory.AllocZeroed((nuint)sizeof(Descriptor));
            descriptor->Size = (nuint)sizeof(Block);
            descriptor->Copy = (nint)(delegate* unmanaged[Cdecl]<Block*, Block*, void>)&Copy;
            descriptor->Dispose = (nint)(delegate* unmanaged[Cdecl]<Block*, void>)&Dispose;
            descriptor->Signature = Marshal.StringToCoTaskMemUTF8("v@?q");
            return descriptor;
        }
        internal static nint Create(Action<long> callback)
        {
            var context = GCHandle.Alloc(callback);
            try
            {
                Block stack = new() { Isa = StackBlock, Flags = (1 << 25) | (1 << 30),
                    Invoke = (nint)(delegate* unmanaged[Cdecl]<Block*, long, void>)&Invoke,
                    Descriptor = (nint)Description, Context = GCHandle.ToIntPtr(context) };
                nint copy = BlockCopy((nint)(&stack));
                return copy != 0 ? copy : throw new InvalidOperationException("Could not retain AppKit completion block.");
            }
            finally { context.Free(); }
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Copy(Block* destination, Block* source) => destination->Context =
            GCHandle.ToIntPtr(GCHandle.Alloc(GCHandle.FromIntPtr(source->Context).Target));
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Dispose(Block* block) => GCHandle.FromIntPtr(block->Context).Free();
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Invoke(Block* block, long response) => ((Action<long>)GCHandle.FromIntPtr(block->Context).Target!)(response);
        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_copy")] private static partial nint BlockCopy(nint block);
        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_release")] internal static partial void Release(nint block);
    }
    private static ValueTask OnOwnerAsync(DesktopPickerOwner owner, Action<nint> action, CancellationToken cancellationToken = default) =>
        owner.InvokeAsync(window => { using var pool = new AutoreleasePool(); action(window); }, cancellationToken);

    private sealed class AutoreleasePool : IDisposable
    {
        private readonly nint _pool = Send(Send(GetClass("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        public void Dispose() => Send(_pool, Sel("drain"));
    }

    private static bool IsSandboxed()
    {
        nint task = SecTaskCreateFromSelf(0);
        if (task == 0) return true; // Unknown entitlement state cannot authorize sibling writes.
        nint key = String("com.apple.security.app-sandbox");
        try
        {
            nint value = SecTaskCopyValueForEntitlement(task, key, out nint error);
            try { return error != 0 || value != 0 && CFBooleanGetValue(value) != 0; }
            finally { if (value != 0) CFRelease(value); if (error != 0) CFRelease(error); }
        }
        finally { Send(key, Sel("release")); CFRelease(task); }
    }
    private static nint String(string value) => StringSend(Send(GetClass("NSString"), Sel("alloc")), Sel("initWithUTF8String:"), value);
    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")] private static partial nint SecTaskCreateFromSelf(nint allocator);
    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")] private static partial nint SecTaskCopyValueForEntitlement(nint task, nint entitlement, out nint error);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static partial byte CFBooleanGetValue(nint value);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static partial void CFRelease(nint value);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint GetClass(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial nint Send(nint receiver, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial byte SendBool(nint receiver, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial void SendArg(nint receiver, nint selector, nint argument);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial void BeginSheet(nint receiver, nint selector, nint window, nint completion);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)] private static partial nint StringSend(nint receiver, nint selector, string value);
}
