using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

internal sealed class WindowsFileLauncher(INativePickerOwner owner) : IDesktopFileLauncher
{
    public async ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.FilePath(path, operation);
        if (operation == DesktopFileOperation.Open && StartsProgram(path)) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied);
        PlatformResult<PlatformUnit> result = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        try
        {
            await owner.InvokeAsync(window =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
                result = Launch(window, path, operation);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OwnerClosedException) { }
        return result;
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static unsafe PlatformResult<PlatformUnit> Launch(nint window, string path, DesktopFileOperation operation)
    {
        var hwnd = new HWND(window);
        // Owner must be STA for shell UI.
        if (PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED).Value < 0)
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        try
        {
            fixed (char* file = path)
            {
                if (operation == DesktopFileOperation.Open)
                {
                    fixed (char* verb = "open")
                    {
                        // Raw overload: the generated FreeLibrarySafeHandle overload would treat the
                        // legacy status code as a module handle and free it.
                        nint status = PInvoke.ShellExecute(hwnd, verb, file, null, null, SHOW_WINDOW_CMD.SW_SHOWNORMAL);
                        return status > 32 ? Success() : new PlatformResult<PlatformUnit>.Failed(
                            status == 5 ? PlatformFailureCode.PermissionDenied : PlatformFailureCode.IoError,
                            new PlatformDiagnostic("ShellExecute", status));
                    }
                }
                if (operation == DesktopFileOperation.ChooseApplication)
                {
                    var info = new OPENASINFO { pcszFile = file, oaifInFlags = OPEN_AS_INFO_FLAGS.OAIF_EXEC }; // Open once.
                    // S_OK also occurs on dismissal on Windows 11. It acknowledges shell
                    // handling; this API cannot reliably prove selection or launch.
                    return FromHResult(PInvoke.SHOpenWithDialog(hwnd, &info).Value);
                }
                ITEMIDLIST* item = null;
                int parsed = PInvoke.SHParseDisplayName(file, null, &item, 0, null).Value;
                if (parsed < 0) return FromHResult(parsed);
                // SHParseDisplayName transfers the absolute PIDL to the caller (CoTaskMemFree).
                try { return FromHResult(PInvoke.SHOpenFolderAndSelectItems(item, 0, null, 0).Value); }
                finally { Marshal.FreeCoTaskMem((nint)item); }
            }
        }
        finally { PInvoke.CoUninitialize(); }
    }
    // The shell's default verb runs programs, scripts, installers and shortcuts, which can
    // target a program themselves. Opening a document must never execute code.
    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msc", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".wsc", ".hta", ".jar", ".py", ".pyw", ".msi", ".msp", ".mst", ".appx", ".appxbundle", ".msix", ".msixbundle",
        ".appinstaller", ".application", ".appref-ms", ".lnk", ".url", ".website", ".scf", ".reg", ".inf", ".chm", ".diagcab",
        ".settingcontent-ms", ".library-ms", ".search-ms", ".searchconnector-ms",
    };
    internal static bool StartsProgram(string path)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        if (Directory.Exists(path)) return false;
        // A symbolic link can give a program a document's name; classify both names.
        string target;
        try { target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path; }
        catch (IOException) { return true; } // An unresolvable link cannot be classified.
        string[] executable = (Environment.GetEnvironmentVariable("PATHEXT") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return IsProgram(path) || IsProgram(target);
        bool IsProgram(string name)
        {
            string extension = Path.GetExtension(name);
            return extension.Length != 0 && (ProgramExtensions.Contains(extension) || executable.Contains(extension, StringComparer.OrdinalIgnoreCase));
        }
    }
    private static PlatformResult<PlatformUnit> Success() => new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private static PlatformResult<PlatformUnit> FromHResult(int result) => result >= 0 ? Success() :
        new PlatformResult<PlatformUnit>.Failed(result switch
        {
            unchecked((int)0x800704C7) => PlatformFailureCode.UserDismissed,
            unchecked((int)0x80070005) => PlatformFailureCode.PermissionDenied,
            _ => PlatformFailureCode.IoError
        }, PlatformDiagnostic.FromHResult(result));
}
