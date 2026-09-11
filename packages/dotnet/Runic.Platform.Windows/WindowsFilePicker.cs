using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// Generated unmanaged Common Item Dialog bindings preserve NativeAOT support.
internal sealed partial class WindowsFilePicker(INativePickerOwner owner) : INativeFilePicker
{
    private const int Cancelled = unchecked((int)0x800704C7);
    internal TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<NativeFileSelection?> SelectAsync(bool save, string? suggestedName, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new NativeBackendUnavailableException();
        var generation = owner.Generation;
        nint dialog = 0;
        NativeFileSelection? selection = null;
        // Close runs through the same STA queue serviced by Show's modal loop.
        // Never return until Show unwinds and releases both COM interfaces.
        Task cancellation = Task.CompletedTask;
        using var registration = cancellationToken.Register(() => cancellation = CloseAsync());
        async Task CloseAsync()
        {
            try { await owner.InvokeAsync(_ => Close(dialog), CancellationToken.None).ConfigureAwait(false); }
            catch (OwnerClosedException) { }
            catch (ObjectDisposedException) { }
        }
        try
        {
            await owner.InvokeAsync(handle =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (handle == 0 || !owner.IsAvailable || owner.Generation != generation) throw new OwnerClosedException();
                dialog = Create(save);
                try
                {
                    Configure(dialog, save, suggestedName);
                    Shown.TrySetResult();
                    int result = Show(dialog, handle);
                    if (result == Cancelled) return;
                    Marshal.ThrowExceptionForHR(result);
                    selection = new(GetPath(dialog), null, true);
                }
                finally { Release(dialog); dialog = 0; }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (COMException error) when (error.HResult == unchecked((int)0x80070005))
        { throw new UnauthorizedAccessException("Windows denied file dialog access.", error); }
        catch (COMException error)
        { throw new IOException("The Windows file dialog failed.", error); }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            await cancellation.ConfigureAwait(false);
        }
        // The facade releases late selection if cancellation won the delivery race.
        return selection;
    }

    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows6.1")]
    private static bool IsNativeWindows() => OperatingSystem.IsWindowsVersionAtLeast(6, 1);

    private static unsafe nint Create(bool save)
    {
        if (!IsNativeWindows()) throw new NativeBackendUnavailableException();
        Guid clsid = new(save ? "C0B4E2F3-BA21-4773-8DBA-335EC946EB8B" : "DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        Guid iid = typeof(IFileDialog).GUID;
        void* dialog = null;
        var result = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, &dialog);
        if (result.Value < 0)
        {
            Release((nint)dialog);
            Marshal.ThrowExceptionForHR(result.Value);
        }
        return (nint)dialog;
    }
    private static unsafe void Configure(nint dialog, bool save, string? suggestedName)
    {
        if (!IsNativeWindows()) throw new NativeBackendUnavailableException();
        var native = (IFileDialog*)dialog;
        FILEOPENDIALOGOPTIONS options;
        Marshal.ThrowExceptionForHR(native->GetOptions(&options).Value);
        options |= FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_NOCHANGEDIR |
            FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST | (save ? FILEOPENDIALOGOPTIONS.FOS_OVERWRITEPROMPT : FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST);
        Marshal.ThrowExceptionForHR(native->SetOptions(options).Value);
        if (suggestedName is not null)
            fixed (char* name = suggestedName)
                Marshal.ThrowExceptionForHR(native->SetFileName(name).Value);
    }
    private static unsafe int Show(nint dialog, nint hwnd)
    {
        if (!IsNativeWindows()) throw new NativeBackendUnavailableException();
        return ((IFileDialog*)dialog)->Show(new HWND(hwnd)).Value;
    }
    private static unsafe void Close(nint dialog)
    {
        if (!IsNativeWindows()) throw new NativeBackendUnavailableException();
        if (dialog != 0) Marshal.ThrowExceptionForHR(((IFileDialog*)dialog)->Close(new HRESULT(Cancelled)).Value);
    }
    private static unsafe string GetPath(nint dialog)
    {
        if (!IsNativeWindows()) throw new NativeBackendUnavailableException();
        IShellItem* item = null;
        try
        {
            Marshal.ThrowExceptionForHR(((IFileDialog*)dialog)->GetResult(&item).Value);
            PWSTR path = default;
            try
            {
                Marshal.ThrowExceptionForHR(item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &path).Value);
                return Marshal.PtrToStringUni((nint)path.Value) ?? throw new IOException("The picker returned no filesystem path.");
            }
            finally { Marshal.FreeCoTaskMem((nint)path.Value); }
        }
        finally { Release((nint)item); }
    }
    private static unsafe void Release(nint item)
    {
        if (!IsNativeWindows()) throw new NativeBackendUnavailableException();
        if (item != 0) ((IUnknown*)item)->Release();
    }
}
