using Microsoft.Win32.SafeHandles;
using Runic.Platform;
using Runic.Platform.Linux.Portal;
using TUnit.Core;

// A file the caller opened and checked for its launch (PortalFileLauncher.Preopened, hosted windows' launch of a given
// file): the portal receives that descriptor for any spelling of its full path, and a launch of another path in the same
// flow fails without opening it.
internal sealed class FileLaunchPreopenedTests
{
    [Test, DisplayName("File launch: the portal receives the checked descriptor for other spellings of its path, and nothing for another path")]
    public async Task PreopenedFileAsync()
    {
        var directory = Directory.CreateTempSubdirectory("runic-portal-preopened-");
        try
        {
            var path = Path.Combine(directory.FullName, "notes.txt");
            var other = Path.Combine(directory.FullName, "other.txt");
            await File.WriteAllTextAsync(path, "notes");
            await File.WriteAllTextAsync(other, "other");
            using var checkedFile = File.OpenHandle(path);
            foreach (var spelling in new[] { path, $"{directory.FullName}//notes.txt", $"{directory.FullName}/./notes.txt" })
            {
                var transport = new FakeTransport(new(0, []));
                SafeFileHandle? received = null;
                var launcher = new PortalFileLauncher(new Owner(), fileTransport: (file, _) =>
                {
                    received = file;
                    return transport;
                });
                var result = await LaunchAsync(launcher, (path, checkedFile), spelling);
                Check(result is PlatformResult<PlatformUnit>.Success, $"{spelling}: {result}");
                Check(ReferenceEquals(received, checkedFile) && transport is { Method: "OpenFile", Requests: 1 },
                    $"{spelling}: the portal did not receive the checked descriptor");
            }

            // Another path, also one that names the same directory, is never opened in the checked file's flow.
            foreach (var spelling in new[] { other, $"{directory.FullName}//other.txt" })
            {
                var transport = new FakeTransport(new(0, []));
                var launcher = new PortalFileLauncher(new Owner(), fileTransport: (_, _) => transport);
                var result = await LaunchAsync(launcher, (path, checkedFile), spelling);
                Check(result is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied } && transport.Requests == 0,
                    $"{spelling}: a launch of another path in the checked file's flow reached the portal: {result}");
            }

            // Without a checked file, the launcher opens the path itself.
            var own = new FakeTransport(new(0, []));
            SafeFileHandle? opened = null;
            var plain = new PortalFileLauncher(new Owner(), fileTransport: (file, _) =>
            {
                opened = file;
                return own;
            });
            Check(await plain.LaunchAsync($"{directory.FullName}//notes.txt") is PlatformResult<PlatformUnit>.Success
                && opened is not null && !ReferenceEquals(opened, checkedFile) && own.Requests == 1, "a launch without a checked file");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // The launch in a flow of its own, as the hosted host's launch sets the checked file for its flow only.
    private static Task<PlatformResult<PlatformUnit>> LaunchAsync(PortalFileLauncher launcher, (string Path, SafeFileHandle File) preopened, string path) =>
        Task.Run(async () =>
        {
            PortalFileLauncher.Preopened.Value = preopened;
            return await launcher.LaunchAsync(path);
        });

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Preopened file launch check failed: " + message); }
}
