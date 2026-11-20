using System.Security.AccessControl;
using System.Security.Principal;

namespace Runic.Spelling;

// Files only their user reads (DEV-SEC-88), as dotnet runic dev writes its code page (BrowserEntry.WritePrivateFile):
// 0600 on Unix, and on Windows an ACL of the current user alone, without inherited entries. Directories this package
// creates are 0700 on Unix; one that exists keeps its mode.
internal static class PrivateFiles
{
    internal const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    internal const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // Directory.CreateDirectory(path, mode) gives the mode to the last directory only, so each missing one is created in
    // turn, from the outermost.
    internal static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        var missing = new Stack<string>();
        for (var current = Path.GetFullPath(path); current is not null && !Directory.Exists(current); current = Path.GetDirectoryName(current))
            missing.Push(current);
        while (missing.TryPop(out var directory)) Directory.CreateDirectory(directory, DirectoryMode);
    }

    // Writes content to path as a new file that only the current user can read or write, replacing one left there.
    internal static async Task WriteNewAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        if (File.Exists(path)) File.Delete(path);
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            await using var stream = new FileInfo(path).Create(System.IO.FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096,
                FileOptions.None, security);
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            return;
        }
        await using (var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = FileMode,
        }))
        {
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }
}
