using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// The Recycle Bin through IFileOperation, the shell's own delete with undo, without UI. The shell deletes an item
// permanently when it cannot recycle it (a network share, a removable drive without a Recycle Bin, a file larger than
// the bin, a policy); the progress sink refuses such a deletion before it happens, so the item stays where it was.
internal sealed class WindowsTrash : IDesktopTrash
{
    public ValueTask<PlatformResult<PlatformUnit>> MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.ItemPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<PlatformResult<PlatformUnit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The shell's file operation needs a single-threaded apartment of its own.
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(WindowsSupport.IsAvailable ? Recycle(path) : throw new PlatformNotSupportedException()); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "Runic Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return new(completion.Task);
    }

    private const int E_ABORT = unchecked((int)0x80004004);

    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static unsafe PlatformResult<PlatformUnit> Recycle(string path)
    {
        if (PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED).Value < 0)
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        IFileOperation* operation = null;
        IShellItem* item = null;
        var state = new RecycleState();
        nint sink = RecycleSink.Create(state);
        try
        {
            Guid itemId = typeof(IShellItem).GUID;
            void* created = null;
            fixed (char* value = path)
            {
                int parsed = PInvoke.SHCreateItemFromParsingName(value, null, &itemId, &created).Value;
                if (parsed < 0) return FromHResult(parsed);
            }
            item = (IShellItem*)created;

            Guid clsid = new("3AD05575-8857-4850-9277-11B85BDB8E09"); // CLSID_FileOperation
            Guid operationId = typeof(IFileOperation).GUID;
            void* instance = null;
            int hr = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &operationId, &instance).Value;
            if (hr < 0) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            operation = (IFileOperation*)instance;
            // FOF_NO_UI | FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE: recycle, and never show a confirmation, error or progress.
            hr = operation->SetOperationFlags((FILEOPERATION_FLAGS)(0x0614u | 0x0040u | 0x00080000u)).Value;
            if (hr < 0) return FromHResult(hr);
            uint cookie;
            hr = operation->Advise((IFileOperationProgressSink*)sink, &cookie).Value;
            if (hr < 0) return FromHResult(hr);
            try
            {
                hr = operation->DeleteItem(item, null).Value;
                if (hr >= 0) hr = operation->PerformOperations().Value;
            }
            finally { operation->Unadvise(cookie); }
            if (state.Refused) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            if (state.Result < 0) return FromHResult(state.Result);
            if (hr < 0) return FromHResult(hr);
            global::Windows.Win32.Foundation.BOOL aborted;
            if (operation->GetAnyOperationsAborted(&aborted).Value >= 0 && aborted)
                return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other, PlatformDiagnostic.FromHResult(E_ABORT));
            // The item is gone only if the shell reported deleting it.
            return state.Deleted ? new PlatformResult<PlatformUnit>.Success(default)
                : new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other);
        }
        finally
        {
            if (operation is not null) operation->Release();
            if (item is not null) item->Release();
            RecycleSink.Release(sink);
            PInvoke.CoUninitialize();
        }
    }

    internal static PlatformResult<PlatformUnit> FromHResult(int hr) => new PlatformResult<PlatformUnit>.Failed(hr switch
    {
        unchecked((int)0x80070002) or unchecked((int)0x80070003) => PlatformFailureCode.NotFound,
        unchecked((int)0x80070005) => PlatformFailureCode.PermissionDenied,
        unchecked((int)0x80070020) or unchecked((int)0x80070021) => PlatformFailureCode.ResourceBusy,
        // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL, ERROR_WRITE_FAULT, ERROR_READ_FAULT, ERROR_CRC, ERROR_IO_DEVICE.
        unchecked((int)0x80070027) or unchecked((int)0x80070070) or unchecked((int)0x8007001D) or unchecked((int)0x8007001E)
            or unchecked((int)0x80070017) or unchecked((int)0x8007045D) => PlatformFailureCode.IOError,
        _ => PlatformFailureCode.Other,
    }, PlatformDiagnostic.FromHResult(hr));

    internal sealed class RecycleState
    {
        internal bool Refused, Deleted;
        internal int Result;
    }

    // IFileOperationProgressSink, handwritten: the sink is implemented, not called, and the static unmanaged vtable
    // keeps it NativeAOT-safe. Only PreDeleteItem and PostDeleteItem act; every other method returns S_OK.
    internal static unsafe class RecycleSink
    {
        [StructLayout(LayoutKind.Sequential)] private struct Instance { internal nint Vtable, Context; internal int References; }
        private static readonly Guid SinkId = new("04B0F1A7-9490-44BC-96E1-4296A31252E2");
        private static readonly Guid UnknownId = new("00000000-0000-0000-C000-000000000046");
        private static readonly nint Vtable = CreateVtable();
        private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x80;

        private static nint CreateVtable()
        {
            var table = (nint*)NativeMemory.Alloc((nuint)(19 * sizeof(nint)));
            table[0] = (nint)(delegate* unmanaged[Stdcall]<Instance*, Guid*, nint*, int>)&Query;
            table[1] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint>)&AddRef;
            table[2] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint>)&ReleaseSlot;
            table[3] = (nint)(delegate* unmanaged[Stdcall]<Instance*, int>)&NoArguments;           // StartOperations
            table[4] = (nint)(delegate* unmanaged[Stdcall]<Instance*, int, int>)&Finish;           // FinishOperations
            table[5] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint, nint, nint, int>)&Three; // PreRenameItem
            table[6] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint, nint, nint, int, nint, int>)&Five; // PostRenameItem
            table[7] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint, nint, nint, nint, int>)&Four; // PreMoveItem
            table[8] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint, nint, nint, nint, int, nint, int>)&Six; // PostMoveItem
            table[9] = table[7];                                                                   // PreCopyItem
            table[10] = table[8];                                                                  // PostCopyItem
            table[11] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint, nint, int>)&PreDelete;
            table[12] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint, nint, int, nint, int>)&PostDelete;
            table[13] = table[5];                                                                  // PreNewItem
            table[14] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint, nint, nint, nint, uint, int, nint, int>)&PostNew;
            table[15] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint, uint, int>)&Progress; // UpdateProgress
            table[16] = table[3];                                                                  // ResetTimer
            table[17] = table[3];                                                                  // PauseTimer
            table[18] = table[3];                                                                  // ResumeTimer
            return (nint)table;
        }

        internal static nint Create(RecycleState state)
        {
            var instance = (Instance*)NativeMemory.AllocZeroed((nuint)sizeof(Instance));
            instance->Vtable = Vtable; instance->References = 1; instance->Context = GCHandle.ToIntPtr(GCHandle.Alloc(state));
            return (nint)instance;
        }

        internal static void Release(nint sink) => Decrement((Instance*)sink);

        // Calls the delete notifications as the shell does, for tests on other platforms.
        internal static int PreDeleteForTest(nint sink, uint flags) => PreDeleteCore((Instance*)sink, flags);
        internal static void PostDeleteForTest(nint sink, int hr) => PostDeleteCore((Instance*)sink, hr);

        private static RecycleState State(Instance* self) => (RecycleState)GCHandle.FromIntPtr(self->Context).Target!;

        private static uint Decrement(Instance* self)
        {
            int count = Interlocked.Decrement(ref self->References);
            if (count == 0) { GCHandle.FromIntPtr(self->Context).Free(); NativeMemory.Free(self); }
            return (uint)count;
        }

        private static int PreDeleteCore(Instance* self, uint flags)
        {
            if ((flags & TSF_DELETE_RECYCLE_IF_POSSIBLE) != 0) return 0;
            // The shell would delete the item permanently.
            State(self).Refused = true;
            return E_ABORT;
        }

        private static void PostDeleteCore(Instance* self, int hr)
        {
            var state = State(self);
            state.Result = hr;
            state.Deleted |= hr >= 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Query(Instance* self, Guid* iid, nint* result)
        {
            *result = 0;
            if (*iid != SinkId && *iid != UnknownId) return unchecked((int)0x80004002);
            Interlocked.Increment(ref self->References); *result = (nint)self; return 0;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static uint AddRef(Instance* self) => (uint)Interlocked.Increment(ref self->References);
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static uint ReleaseSlot(Instance* self) => Decrement(self);
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int NoArguments(Instance* self) => 0;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Finish(Instance* self, int hr) => 0;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Three(Instance* self, uint flags, nint a, nint b) => 0;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Four(Instance* self, uint flags, nint a, nint b, nint c) => 0;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Five(Instance* self, uint flags, nint a, nint b, int hr, nint c) => 0;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Six(Instance* self, uint flags, nint a, nint b, nint c, int hr, nint d) => 0;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int PostNew(Instance* self, uint flags, nint a, nint b, nint c, uint attributes, int hr, nint d) => 0;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Progress(Instance* self, uint total, uint done) => 0;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int PreDelete(Instance* self, uint flags, nint item) => PreDeleteCore(self, flags);
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int PostDelete(Instance* self, uint flags, nint item, int hr, nint created) { PostDeleteCore(self, hr); return 0; }
    }
}
