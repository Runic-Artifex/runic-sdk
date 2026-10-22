using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Platform;
using Runic.Platform.MacOS;
using TUnit.Core;
using static Runic.Platform.MacOS.MacDesktopNative;

// NSURL bookmarks: ordinary bookmarks in the test process (CI's macOS runner, JIT and NativeAOT), and security-scoped
// ones in the App Sandbox fixture (tests/native/platform-sandbox), which CI runs with --bookmark-probe (Program.cs).
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed partial class MacBookmarksNativeTests
{
    [Test, Explicit, Category("NativeOpenEvents"), DisplayName("native bookmarks: create, resolve, follow a moved file, report a deleted one"), Timeout(60_000)]
    public async Task BookmarksAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("NSURL bookmarks need macOS.");
        cancellationToken.ThrowIfCancellationRequested();
        await CheckAsync(Path.GetTempPath());
    }

    // The sandboxed fixture: bookmarks of a file in the container are security-scoped; a file outside the container,
    // which the user never chose, gets none.
    internal static int RunSandboxProbe(string outside)
    {
        try
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APP_SANDBOX_CONTAINER_ID")))
                throw new InvalidOperationException("The probe does not run in the App Sandbox.");
            NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out _);
            string container;
            using (new Pool()) container = Text(TemporaryDirectory());
            CheckAsync(container).GetAwaiter().GetResult();
            var refused = MacOSPlatformProvider.CreateFileBookmarks().CreateAsync(outside).AsTask().GetAwaiter().GetResult();
            Check(refused is PlatformResult<ImmutableArray<byte>>.Failed, $"no bookmark for a file outside the container: {refused}");
            Console.WriteLine($"PASS sandboxed bookmarks: security-scoped in the container; outside it {refused}.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error);
            return 1;
        }
    }

    private static async Task CheckAsync(string directory)
    {
        var bookmarks = MacOSPlatformProvider.CreateFileBookmarks();
        string folder = Path.Combine(directory, "runic-bookmarks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "probe é.txt"), moved = Path.Combine(folder, "moved.txt");
            await File.WriteAllTextAsync(file, "probe");
            var created = await bookmarks.CreateAsync(file);
            Check(created.TryGetValue(out var bookmark) && bookmark.Length > 0, $"a bookmark: {created}");

            var resolved = await bookmarks.ResolveAsync(bookmark);
            Check(resolved.TryGetValue(out var access), $"resolves: {resolved}");
            await using (access!)
            {
                Check(Path.GetFileName(access!.LocalPath) == "probe é.txt" && await File.ReadAllTextAsync(access.LocalPath) == "probe" && !access.IsStale,
                    $"resolves to the file: {access.LocalPath} stale={access.IsStale}");
            }

            File.Move(file, moved);
            resolved = await bookmarks.ResolveAsync(bookmark);
            Check(resolved.TryGetValue(out access), $"a moved file resolves: {resolved}");
            await using (access!)
                Check(Path.GetFileName(access!.LocalPath) == "moved.txt", $"the bookmark follows the moved file: {access.LocalPath} stale={access.IsStale}");

            File.Delete(moved);
            resolved = await bookmarks.ResolveAsync(bookmark);
            Check(resolved is PlatformResult<IDesktopBookmarkAccess>.Failed, $"a deleted file does not resolve: {resolved}");
            Check(await bookmarks.CreateAsync(moved) is PlatformResult<ImmutableArray<byte>>.Failed, "no bookmark for a missing file");
            var garbage = await bookmarks.ResolveAsync([1, 2, 3, 4]);
            Check(garbage is PlatformResult<IDesktopBookmarkAccess>.Failed, $"bytes that are not a bookmark: {garbage}");
            Console.WriteLine($"PASS bookmarks: round trip, moved file, deleted file ({resolved}), invalid bytes ({garbage}).");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [LibraryImport("/System/Library/Frameworks/Foundation.framework/Foundation", EntryPoint = "NSTemporaryDirectory")] private static partial nint TemporaryDirectory();

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
