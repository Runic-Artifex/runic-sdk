using System.Runtime.InteropServices;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

internal sealed partial class WindowsFileLauncher(INativePickerOwner owner) : IDesktopFileLauncher
{
    public async ValueTask<PlatformResult<Unit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.FilePath(path, operation);
        PlatformResult<Unit> result = new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed);
        try
        {
            await owner.InvokeAsync(window =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                int initialized = CoInitializeEx(0, 2); // Owner must be STA for shell UI.
                if (initialized < 0) { result = new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable); return; }
                try
                {
                    if (operation == DesktopFileOperation.Open)
                    {
                        var status = ShellExecute(window, "open", path, null, null, 1);
                        result = status > 32 ? Success() : new PlatformResult<Unit>.Failed(status == 5 ? FailureCode.PermissionDenied : FailureCode.IoError);
                    }
                    else if (operation == DesktopFileOperation.ChooseApplication)
                    {
                        unsafe
                        {
                            fixed (char* fileName = path)
                            {
                                var info = new OpenWithInfo { File = (nint)fileName, Flags = 4 }; // OAIF_EXEC: open once.
                                result = FromHResult(SHOpenWithDialog(window, in info));
                            }
                        }
                    }
                    else
                    {
                        int status = SHParseDisplayName(path, 0, out var item, 0, out _);
                        if (status < 0) { result = FromHResult(status); return; }
                        try { result = FromHResult(SHOpenFolderAndSelectItems(item, 0, 0, 0)); }
                        finally { Marshal.FreeCoTaskMem(item); }
                    }
                }
                finally { CoUninitialize(); }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OwnerClosedException) { }
        return result;
    }
    private static PlatformResult<Unit> Success() => new PlatformResult<Unit>.Success(new Unit());
    private static PlatformResult<Unit> FromHResult(int result) => result >= 0 ? Success() :
        new PlatformResult<Unit>.Failed(result switch
        {
            unchecked((int)0x800704C7) => FailureCode.UserDismissed,
            unchecked((int)0x80070005) => FailureCode.PermissionDenied,
            _ => FailureCode.IoError
        });
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenWithInfo
    {
        internal nint File, Class;
        internal uint Flags;
    }
    [LibraryImport("shell32.dll")] private static partial int SHOpenWithDialog(nint owner, in OpenWithInfo info);
    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)] private static partial nint ShellExecute(nint owner, string operation, string file, string? parameters, string? directory, int show);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int SHParseDisplayName(string path, nint bindContext, out nint item, uint attributes, out uint result);
    [LibraryImport("shell32.dll")] private static partial int SHOpenFolderAndSelectItems(nint item, uint count, nint children, uint flags);
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint mode);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
}
