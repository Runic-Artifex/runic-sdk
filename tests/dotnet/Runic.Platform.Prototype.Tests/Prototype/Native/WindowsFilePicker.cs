using System.Runtime.InteropServices;

namespace Runic.Platform.Prototype;

// Common Item Dialog ABI, without reflection-based COM marshalling (NativeAOT).
internal sealed partial class WindowsFilePicker(DesktopPickerOwner owner) : INativeFilePicker
{
    private const int Cancelled = unchecked((int)0x800704C7);
    internal TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<NativeFileSelection?> SelectAsync(bool save, string? suggestedName, CancellationToken cancellationToken)
    {
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
        await owner.InvokeAsync(handle =>
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        await registration.DisposeAsync().ConfigureAwait(false);
        await cancellation.ConfigureAwait(false);
        // The facade releases late selection if cancellation won the delivery race.
        return selection;
    }

    private static unsafe nint Create(bool save)
    {
        Guid clsid = new(save ? "C0B4E2F3-BA21-4773-8DBA-335EC946EB8B" : "DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        Guid iid = new("42F85136-DB7E-439C-85F1-E4075D135FC8");
        nint dialog;
        Marshal.ThrowExceptionForHR(CoCreateInstance(&clsid, 0, 1, &iid, &dialog));
        return dialog;
    }
    private static unsafe void Configure(nint dialog, bool save, string? suggestedName)
    {
        var table = *(nint**)dialog;
        uint options;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)table[10])(dialog, &options));
        // Force filesystem items, preserve the process working directory, require
        // existing parent; open also requires an existing file. Save prompts overwrite.
        options |= 0x40 | 0x8 | 0x800 | (save ? 0x2u : 0x1000u);
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, int>)table[9])(dialog, options));
        if (suggestedName is not null)
            fixed (char* name = suggestedName)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)table[15])(dialog, name));
    }
    private static unsafe int Show(nint dialog, nint hwnd) => ((delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)dialog)[3])(dialog, hwnd);
    private static unsafe void Close(nint dialog)
    {
        if (dialog != 0) Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int>)(*(nint**)dialog)[23])(dialog, Cancelled));
    }
    private static unsafe string GetPath(nint dialog)
    {
        nint item;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)dialog)[20])(dialog, &item));
        try
        {
            nint path;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)item)[5])(item, 0x80058000, &path));
            try { return Marshal.PtrToStringUni(path) ?? throw new IOException("The picker returned no filesystem path."); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        finally { Release(item); }
    }
    private static unsafe void Release(nint item) { if (item != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)item)[2])(item); }
    [LibraryImport("ole32.dll")]
    private static unsafe partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);
}
