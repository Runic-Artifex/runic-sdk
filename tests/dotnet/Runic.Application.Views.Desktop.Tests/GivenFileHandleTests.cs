using Runic.Application.Views.Desktop.Hosting;
using TUnit.Core;

// window-services/1, launch (host-protocol.md, T16): a file the user gave a guest is opened for its launch. On Linux the
// descriptor stays on the checked file when the guest renames another one over its path; on Windows the held handle
// keeps the path from being renamed, replaced or deleted during the launch.
internal sealed class GivenFileHandleTests
{
    [Test]
    public async Task TheOpenedFileIsTheGivenOneWhateverHappensToItsPath()
    {
        if (!GivenFileHandle.IsSupported) Skip.Test("macOS launches by path: the host checks the given file's identity right before the launch.");
        var directory = Directory.CreateTempSubdirectory("runic-given-file-");
        try
        {
            var given = Path.Combine(directory.FullName, "report.txt");
            var other = Path.Combine(directory.FullName, "payload.txt");
            await File.WriteAllTextAsync(given, "report");
            await File.WriteAllTextAsync(other, "payload");
            var givenIdentity = Runic.Application.Views.DirectoryIdentity.Read(given);
            var otherIdentity = Runic.Application.Views.DirectoryIdentity.Read(other);
            Check(givenIdentity is not null && otherIdentity is not null, "The files have no identity.");

            using (var opened = GivenFileHandle.Open(given, out var inUse))
            {
                Check(opened is not null && !inUse, "The given file did not open.");
                Check(opened!.IsFile(givenIdentity) && !opened.IsFile(otherIdentity), $"The opened file's identity {opened!.Identity} is not the given file's {givenIdentity}.");
                if (OperatingSystem.IsWindows())
                {
                    // The path keeps its file while the host holds it.
                    Check(Throws(() => File.Move(other, given, overwrite: true)), "Another file replaced the held one.");
                    Check(Throws(() => File.Move(given, Path.Combine(directory.FullName, "moved.txt"))), "The held file was renamed.");
                    Check(Throws(() => File.Delete(given)), "The held file was deleted.");
                }
                else
                {
                    // The descriptor stays on the checked file, which the portal receives instead of the path.
                    File.Move(other, given, overwrite: true);
                    Check(opened.IsFile(givenIdentity) && !opened.IsFile(Runic.Application.Views.DirectoryIdentity.Read(given)),
                        "The descriptor followed the path instead of the file.");
                }
            }

            // A file that another process writes is still opened, shared for write only.
            if (OperatingSystem.IsWindows())
            {
                await using var writer = new FileStream(given, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                using var opened = GivenFileHandle.Open(given, out var inUse);
                Check(opened is not null && !inUse, "A file another process writes did not open.");
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static bool Throws(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
        return false;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
