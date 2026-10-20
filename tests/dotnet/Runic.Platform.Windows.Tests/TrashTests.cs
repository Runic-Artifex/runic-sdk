using System.Text;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

internal sealed class TrashTests
{
    // Error mapping and the progress sink's refusal; runs on every OS.
    [Test]
    public void ErrorsAndRefusal()
    {
#pragma warning disable CA1416 // FromHResult and the sink are pure; nothing here calls the shell.
        Check(WindowsTrash.FromHResult(unchecked((int)0x80070002)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }, "file not found");
        Check(WindowsTrash.FromHResult(unchecked((int)0x80070003)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }, "path not found");
        Check(WindowsTrash.FromHResult(unchecked((int)0x80070005)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied, Diagnostic.Domain: "HRESULT" }, "access denied");
        Check(WindowsTrash.FromHResult(unchecked((int)0x80070020)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy }, "sharing violation");
        Check(WindowsTrash.FromHResult(unchecked((int)0x80004005)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Other }, "other errors");

        var state = new WindowsTrash.RecycleState();
        nint sink = WindowsTrash.RecycleSink.Create(state);
        try
        {
            // TSF_DELETE_RECYCLE_IF_POSSIBLE: the shell will recycle the item.
            Check(WindowsTrash.RecycleSink.PreDeleteForTest(sink, 0x80) == 0 && !state.Refused, "a recycling delete proceeds");
            // Without it the shell would delete permanently: E_ABORT stops it.
            Check(WindowsTrash.RecycleSink.PreDeleteForTest(sink, 0) == unchecked((int)0x80004004) && state.Refused, "a permanent delete is refused");
            WindowsTrash.RecycleSink.PostDeleteForTest(sink, 0);
            Check(state.Deleted && state.Result == 0, "a completed delete is recorded");
        }
        finally { WindowsTrash.RecycleSink.Release(sink); }
#pragma warning restore CA1416
    }

    // The runner's Recycle Bin. Each item has a name unique to this run and is removed from the bin afterwards.
    [Test, Explicit, Category("NativeTrash")]
    public async Task RecycleBinAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) throw new PlatformNotSupportedException("The Recycle Bin requires Windows.");
        string unique = "runic-trash-" + Guid.NewGuid().ToString("N")[..8];
        string root = Path.Combine(Path.GetTempPath(), unique);
        Directory.CreateDirectory(Path.Combine(root, "folder", "sub"));
        string file = Path.Combine(root, unique + " ä.txt");
        File.WriteAllText(file, "content");
        File.WriteAllText(Path.Combine(root, "folder", "sub", "leaf.txt"), "leaf");
        var trash = WindowsPlatformProvider.CreateTrash();
        try
        {
            Check(await trash.MoveToTrashAsync(file) is PlatformResult<PlatformUnit>.Success && !File.Exists(file), "the file was recycled");
            Check(await trash.MoveToTrashAsync(Path.Combine(root, "folder")) is PlatformResult<PlatformUnit>.Success && !Directory.Exists(Path.Combine(root, "folder")),
                "the directory was recycled");
            var recycled = Recycled(root);
            Check(recycled.Contains(file, StringComparer.OrdinalIgnoreCase) && recycled.Contains(Path.Combine(root, "folder"), StringComparer.OrdinalIgnoreCase),
                $"the Recycle Bin records both original paths: {string.Join(", ", recycled)}");
            Check(await trash.MoveToTrashAsync(Path.Combine(root, "missing.txt")) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }, "missing");

            // An administrative share reaches the same disk over SMB, where nothing can be recycled: the shell would
            // delete permanently, and the request must leave the file instead.
            string share = @"\\localhost\" + root[0] + "$" + root[2..];
            if (Directory.Exists(share))
            {
                string remote = Path.Combine(share, "remote.txt");
                File.WriteAllText(remote, "kept");
                var result = await trash.MoveToTrashAsync(remote);
                Console.WriteLine($"Recycling over SMB: {result}");
                Check(result is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable } && File.Exists(remote),
                    "an item that cannot be recycled stays where it was");
            }
            else Console.WriteLine($"Skipped the SMB case: {share} is not reachable.");
            Console.WriteLine("PASS Windows Recycle Bin: file, directory, missing item, unrecyclable item.");
        }
        finally
        {
            RemoveRecycled(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // The bin keeps a $I file per item, beside its $R content: a version, the size, the deletion time, then the
    // original path's length and the path in UTF-16.
    private static List<string> Recycled(string root) => [.. Entries(root).Select(entry => entry.Original)];

    private static void RemoveRecycled(string root)
    {
        foreach (var (info, _) in Entries(root))
        {
            string content = Path.Combine(Path.GetDirectoryName(info)!, "$R" + Path.GetFileName(info)[2..]);
            if (Directory.Exists(content)) Directory.Delete(content, recursive: true);
            else File.Delete(content);
            File.Delete(info);
        }
    }

    private static IEnumerable<(string Info, string Original)> Entries(string root)
    {
        string bin = Path.Combine(Path.GetPathRoot(root)!, "$Recycle.Bin");
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (string info in Directory.EnumerateFiles(bin, "$I*", options))
        {
            byte[] data;
            try { data = File.ReadAllBytes(info); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            if (data.Length < 28 || BitConverter.ToInt64(data, 0) != 2) continue;
            int length = BitConverter.ToInt32(data, 24);
            if (data.Length < 28 + length * 2) continue;
            string original = Encoding.Unicode.GetString(data, 28, length * 2).TrimEnd('\0');
            if (original.StartsWith(root, StringComparison.OrdinalIgnoreCase)) yield return (info, original);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Trash check failed: " + message);
    }
}
