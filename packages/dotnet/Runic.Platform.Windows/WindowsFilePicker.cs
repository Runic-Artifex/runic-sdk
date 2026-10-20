using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// Generated unmanaged Common Item Dialog bindings preserve NativeAOT support.
internal sealed partial class WindowsFilePicker(INativeWindowOwner owner) : INativeFilePicker, INativeDirectoryPicker
{
    private const int Cancelled = unchecked((int)0x800704C7);
    internal TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool SupportsMultipleSelection => true;

    public ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SelectCoreAsync(request, false, cancellationToken);
    }

    public async ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken)
    {
        var selections = await SelectCoreAsync(new NativeFileDialogRequest(false, null) { InitialDirectory = initialDirectory }, true, cancellationToken)
            .ConfigureAwait(false);
        return selections is [var selection] ? new(selection.Path, selection.Access) : null;
    }

    private async ValueTask<IReadOnlyList<NativeFileSelection>?> SelectCoreAsync(NativeFileDialogRequest request, bool directory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new NativeBackendUnavailableException();
        bool save = request.Save, multiple = request.AllowMultiple && !save && !directory;
        var generation = owner.Generation;
        nint dialog = 0;
        IReadOnlyList<NativeFileSelection>? selection = null;
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
                    Configure(dialog, save, directory, multiple, request);
                    Shown.TrySetResult();
                    int result = Show(dialog, handle);
                    if (result == Cancelled) return;
                    Marshal.ThrowExceptionForHR(result);
                    selection = multiple ? GetPaths(dialog) : [new(GetPath(dialog), null, true)];
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
    private static unsafe void Configure(nint dialog, bool save, bool directory, bool multiple, NativeFileDialogRequest request)
    {
        if (!IsNativeWindows()) throw new NativeBackendUnavailableException();
        var native = (IFileDialog*)dialog;
        FILEOPENDIALOGOPTIONS options;
        Marshal.ThrowExceptionForHR(native->GetOptions(&options).Value);
        options |= FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_NOCHANGEDIR |
            FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST | (save ? FILEOPENDIALOGOPTIONS.FOS_OVERWRITEPROMPT : FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST);
        if (directory) options |= FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS;
        if (multiple) options |= FILEOPENDIALOGOPTIONS.FOS_ALLOWMULTISELECT;
        Marshal.ThrowExceptionForHR(native->SetOptions(options).Value);
        if (!directory && FilterSpecs(request.Filters) is { Length: > 0 } specs)
        {
            // The dialog copies the strings during SetFileTypes; pin them only for the call.
            var handles = new List<GCHandle>(specs.Length * 2);
            try
            {
                var nativeSpecs = new COMDLG_FILTERSPEC[specs.Length];
                for (int index = 0; index < specs.Length; index++)
                {
                    var name = GCHandle.Alloc(specs[index].Name + "\0", GCHandleType.Pinned);
                    var spec = GCHandle.Alloc(specs[index].Spec + "\0", GCHandleType.Pinned);
                    handles.Add(name); handles.Add(spec);
                    nativeSpecs[index] = new COMDLG_FILTERSPEC { pszName = (char*)name.AddrOfPinnedObject(), pszSpec = (char*)spec.AddrOfPinnedObject() };
                }
                fixed (COMDLG_FILTERSPEC* filters = nativeSpecs)
                    Marshal.ThrowExceptionForHR(native->SetFileTypes((uint)nativeSpecs.Length, filters).Value);
                Marshal.ThrowExceptionForHR(native->SetFileTypeIndex(1).Value); // One-based: the first filter.
            }
            finally { foreach (var handle in handles) handle.Free(); }
        }
        // SetFolder overrides the folder Windows remembers for the application. A folder that does not exist or
        // cannot be parsed leaves the dialog's own choice: the request is a hint.
        if (request.InitialDirectory is { } folder && CreateItem(folder) is var item && item != null)
        {
            try { _ = native->SetFolder(item); }
            finally { Release((nint)item); }
        }
        if (request.SuggestedName is { } suggestedName)
            fixed (char* name = suggestedName)
                Marshal.ThrowExceptionForHR(native->SetFileName(name).Value);
    }

    // One Windows filter specification per filter: "*.png;*.jpg", or "*.*" for every file.
    internal static (string Name, string Spec)[] FilterSpecs(ImmutableArray<FileDialogFilter> filters) => filters.IsDefaultOrEmpty ? []
        : [.. filters.Select(static filter => (filter.Name,
            filter.MatchesAllFiles ? "*.*" : string.Join(';', filter.Extensions.Select(static extension => "*." + extension))))];

    private static unsafe IShellItem* CreateItem(string path)
    {
        if (!IsNativeWindows() || !Directory.Exists(path)) return null;
        Guid iid = typeof(IShellItem).GUID;
        void* item = null;
        fixed (char* value = path)
            return PInvoke.SHCreateItemFromParsingName(value, null, &iid, &item).Value < 0 ? null : (IShellItem*)item;
    }

    private static unsafe List<NativeFileSelection> GetPaths(nint dialog)
    {
        if (!IsNativeWindows()) throw new NativeBackendUnavailableException();
        Guid iid = typeof(IFileOpenDialog).GUID;
        void* open = null;
        Marshal.ThrowExceptionForHR(((IUnknown*)dialog)->QueryInterface(&iid, &open).Value);
        IShellItemArray* items = null;
        try
        {
            Marshal.ThrowExceptionForHR(((IFileOpenDialog*)open)->GetResults(&items).Value);
            uint count;
            Marshal.ThrowExceptionForHR(items->GetCount(&count).Value);
            var paths = new List<NativeFileSelection>((int)count);
            for (uint index = 0; index < count; index++)
            {
                IShellItem* item = null;
                Marshal.ThrowExceptionForHR(items->GetItemAt(index, &item).Value);
                try { paths.Add(new(GetPath(item), null, true)); }
                finally { Release((nint)item); }
            }
            return paths.Count > 0 ? paths : throw new IOException("The picker returned no filesystem path.");
        }
        finally
        {
            Release((nint)items);
            Release((nint)open);
        }
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
            return GetPath(item);
        }
        finally { Release((nint)item); }
    }

    private static unsafe string GetPath(IShellItem* item)
    {
        if (!IsNativeWindows()) throw new NativeBackendUnavailableException();
        PWSTR path = default;
        try
        {
            Marshal.ThrowExceptionForHR(item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &path).Value);
            return Marshal.PtrToStringUni((nint)path.Value) ?? throw new IOException("The picker returned no filesystem path.");
        }
        finally { Marshal.FreeCoTaskMem((nint)path.Value); }
    }
    private static unsafe void Release(nint item)
    {
        if (!IsNativeWindows()) throw new NativeBackendUnavailableException();
        if (item != 0) ((IUnknown*)item)->Release();
    }
}
