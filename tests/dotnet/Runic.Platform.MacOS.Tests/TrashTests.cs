using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

internal sealed class TrashTests
{
    [Test, DisplayName("trash: NSFileManager errors map to typed outcomes")]
    public void Errors()
    {
        Check(MacTrash.Map(new("NSCocoaErrorDomain", 3328)) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "no trash on the volume");
        Check(MacTrash.Map(new("NSCocoaErrorDomain", 4)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound, Diagnostic.Code: 4 }, "no such file");
        Check(MacTrash.Map(new("NSPOSIXErrorDomain", 2)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }, "ENOENT");
        Check(MacTrash.Map(new("NSCocoaErrorDomain", 513)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "no permission");
        Check(MacTrash.Map(new("NSPOSIXErrorDomain", 13)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "EACCES");
        Check(MacTrash.Map(new("NSCocoaErrorDomain", 512)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.IoError, Diagnostic.Domain: "NSCocoaErrorDomain" }, "other errors");
    }

    // The runner's Trash. Each item has a name unique to this run and is removed from the Trash afterwards.
    [Test, Explicit, Category("NativeTrash"), DisplayName("trash: files, directories and links move to the Trash")]
    public async Task TrashAsync()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The Trash requires macOS.");
        string unique = "runic-trash-" + Guid.NewGuid().ToString("N")[..8];
        string root = Path.Combine(Path.GetTempPath(), unique);
        Directory.CreateDirectory(Path.Combine(root, "folder", "sub"));
        File.WriteAllText(Path.Combine(root, "folder", "sub", "leaf.txt"), "leaf");
        string file = Path.Combine(root, unique + " ä.txt");
        File.WriteAllText(file, "content");
        string link = Path.Combine(root, "link");
        File.CreateSymbolicLink(link, file);
        var trashed = new List<string>();
        try
        {
            // The resulting path lets the test remove what it trashed; the provider returns only the outcome.
            var (linkResult, linkPath) = MacTrash.Trash(link);
            if (linkPath is not null) trashed.Add(linkPath);
            Check(linkResult is PlatformResult<PlatformUnit>.Success && new FileInfo(link).LinkTarget is null && !File.Exists(link) && File.Exists(file),
                $"the link moved, not its target: {linkResult}");
            var (fileResult, filePath) = MacTrash.Trash(file);
            if (filePath is not null) trashed.Add(filePath);
            Check(fileResult is PlatformResult<PlatformUnit>.Success && !File.Exists(file), $"the file moved: {fileResult}");
            Console.WriteLine($"Trashed to {filePath}");
            var (folderResult, folderPath) = MacTrash.Trash(Path.Combine(root, "folder"));
            if (folderPath is not null) trashed.Add(folderPath);
            Check(folderResult is PlatformResult<PlatformUnit>.Success && !Directory.Exists(Path.Combine(root, "folder")), $"the directory moved: {folderResult}");

            var trash = MacOSPlatformProvider.CreateTrash();
            var missing = await trash.MoveToTrashAsync(Path.Combine(root, "missing.txt"));
            Check(missing is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }, $"missing: {missing}");
            File.WriteAllText(file, "again");
            Check(await trash.MoveToTrashAsync(file) is PlatformResult<PlatformUnit>.Success && !File.Exists(file), "through the provider");
            Console.WriteLine("PASS macOS Trash: link, file, directory, missing item.");
        }
        finally
        {
            foreach (string path in trashed)
            {
                try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); else File.Delete(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.WriteLine($"Could not remove {path}: {error.Message}"); }
            }
            // The item trashed through the provider has no known path; it carries this run's unique name.
            string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
            try { foreach (string path in Directory.EnumerateFiles(home, unique + "*")) File.Delete(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.WriteLine($"Could not search the Trash: {error.Message}"); }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Trash check failed: " + message);
    }
}
