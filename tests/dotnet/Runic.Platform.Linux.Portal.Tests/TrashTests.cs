using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Runic.Platform;
using Runic.Platform.Linux.Portal;
using TUnit.Core;
using Tmds.DBus.Protocol;

// Every case uses a trash in a temporary directory, never the user's.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed partial class TrashTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Test, DisplayName("trash: a file goes to files/ with an info file recording its path and deletion date")]
    public async Task HomeTrashAsync()
    {
        using var scratch = new Scratch();
        string item = scratch.File("items/a b%ä.txt", "content");
        var trash = new LinuxTrash(homeTrash: scratch.HomeTrash, sandboxed: false);

        Check(await trash.MoveToTrashAsync(item) is PlatformResult<PlatformUnit>.Success, "trashed");
        Check(!File.Exists(item), "the item left its directory");
        Check(File.ReadAllText(Path.Combine(scratch.HomeTrash, "files", "a b%ä.txt")) == "content", "the item is in files/");
        string info = File.ReadAllText(Path.Combine(scratch.HomeTrash, "info", "a b%ä.txt.trashinfo"));
        string expected = "[Trash Info]\nPath=" + LinuxTrash.Escape(item) + "\nDeletionDate=";
        Check(info.StartsWith(expected, StringComparison.Ordinal) && info.Contains("a%20b%25%C3%A4.txt\n", StringComparison.Ordinal), $"info: {info}");
        Check(System.Text.RegularExpressions.Regex.IsMatch(info, @"\nDeletionDate=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\n$"), $"deletion date: {info}");
        Check((File.GetUnixFileMode(Path.Combine(scratch.HomeTrash, "info")) & (UnixFileMode)0x3F) == 0, "the trash is private to the user");
    }

    [Test, DisplayName("trash: a second item of the same name, and a stale entry without info, get the next name")]
    public async Task NameCollisionAsync()
    {
        using var scratch = new Scratch();
        var trash = new LinuxTrash(homeTrash: scratch.HomeTrash, sandboxed: false);
        Check(await trash.MoveToTrashAsync(scratch.File("one/report.tar.gz", "1")) is PlatformResult<PlatformUnit>.Success, "first");
        Check(await trash.MoveToTrashAsync(scratch.File("two/report.tar.gz", "2")) is PlatformResult<PlatformUnit>.Success, "second");
        Check(File.ReadAllText(Path.Combine(scratch.HomeTrash, "files", "report.tar.gz")) == "1"
            && File.ReadAllText(Path.Combine(scratch.HomeTrash, "files", "report.2.tar.gz")) == "2", "the second is report.2.tar.gz");
        Check(File.ReadAllText(Path.Combine(scratch.HomeTrash, "info", "report.2.tar.gz.trashinfo")).Contains("/two/report.tar.gz\n", StringComparison.Ordinal),
            "its info records its own path");

        // An entry in files/ with no info file, left by a crashed implementation, is not replaced, also when it is an empty directory.
        Directory.CreateDirectory(Path.Combine(scratch.HomeTrash, "files", "stale"));
        string stale = Path.Combine(scratch.Root, "three", "stale");
        Directory.CreateDirectory(stale);
        Check(await trash.MoveToTrashAsync(stale) is PlatformResult<PlatformUnit>.Success, "stale name");
        Check(Directory.Exists(Path.Combine(scratch.HomeTrash, "files", "stale.2")) && !File.Exists(Path.Combine(scratch.HomeTrash, "info", "stale.trashinfo")),
            "the stale entry keeps its name and no info is left for it");
    }

    [Test, DisplayName("trash: a directory moves with its contents, a symbolic link moves itself")]
    public async Task DirectoriesAndLinksAsync()
    {
        using var scratch = new Scratch();
        var trash = new LinuxTrash(homeTrash: scratch.HomeTrash, sandboxed: false);
        scratch.File("tree/sub/leaf.txt", "leaf");
        Check(await trash.MoveToTrashAsync(Path.Combine(scratch.Root, "tree") + "/") is PlatformResult<PlatformUnit>.Success, "directory");
        Check(File.ReadAllText(Path.Combine(scratch.HomeTrash, "files", "tree", "sub", "leaf.txt")) == "leaf", "the contents moved");

        string target = scratch.File("target.txt", "target");
        string link = Path.Combine(scratch.Root, "link");
        File.CreateSymbolicLink(link, target);
        string broken = Path.Combine(scratch.Root, "broken");
        File.CreateSymbolicLink(broken, Path.Combine(scratch.Root, "missing"));
        Check(await trash.MoveToTrashAsync(link) is PlatformResult<PlatformUnit>.Success
            && await trash.MoveToTrashAsync(broken) is PlatformResult<PlatformUnit>.Success, "links");
        Check(File.Exists(target) && new FileInfo(Path.Combine(scratch.HomeTrash, "files", "link")).LinkTarget == target
            && new FileInfo(Path.Combine(scratch.HomeTrash, "files", "broken")).LinkTarget is not null, "the links moved, not their targets");

        // A link in the parent is resolved, so the info records where the item really was.
        string real = Path.Combine(scratch.Root, "real");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(Path.Combine(scratch.Root, "alias"), real);
        File.WriteAllText(Path.Combine(real, "x.txt"), "x");
        Check(await trash.MoveToTrashAsync(Path.Combine(scratch.Root, "alias", "x.txt")) is PlatformResult<PlatformUnit>.Success, "through a link");
        Check(File.ReadAllText(Path.Combine(scratch.HomeTrash, "info", "x.txt.trashinfo")).Contains(LinuxTrash.Escape(Path.Combine(real, "x.txt")) + "\n", StringComparison.Ordinal),
            "the resolved path is recorded");
    }

    [Test, DisplayName("trash: a missing item, a refused move and the trash's own parent leave nothing behind")]
    public async Task FailuresAsync()
    {
        using var scratch = new Scratch();
        var trash = new LinuxTrash(homeTrash: scratch.HomeTrash, sandboxed: false);
        Check(await trash.MoveToTrashAsync(Path.Combine(scratch.Root, "missing")) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound },
            "missing");
        Check(await trash.MoveToTrashAsync(Path.Combine(scratch.Root, "no", "such", "dir")) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound },
            "missing parent");

        if (GetEffectiveUserId() != 0)
        {
            string locked = Path.Combine(scratch.Root, "locked");
            string item = scratch.File("locked/kept.txt", "kept");
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                Check(await trash.MoveToTrashAsync(item) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied } && File.Exists(item),
                    "a read-only directory keeps its item");
            }
            finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }

        // The trash lives inside the item: a directory cannot move into itself.
        string data = Path.Combine(scratch.Root, "inner");
        var inner = new LinuxTrash(homeTrash: Path.Combine(data, "Trash"), sandboxed: false);
        Directory.CreateDirectory(data);
        Check(await inner.MoveToTrashAsync(data) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData } && Directory.Exists(data),
            "the trash's parent stays");

        Check(Directory.GetFiles(Path.Combine(scratch.HomeTrash, "info")).Length == 0 && Directory.GetFileSystemEntries(Path.Combine(scratch.HomeTrash, "files")).Length == 0,
            "no info or item is left by a failure");
        Check(Directory.GetFiles(Path.Combine(data, "Trash", "info")).Length == 0, "no info is left in the inner trash");
        Check(await Catch(() => trash.MoveToTrashAsync("relative").AsTask()) is ArgumentException
            && await Catch(() => trash.MoveToTrashAsync("/").AsTask()) is ArgumentException
            && await Catch(() => trash.MoveToTrashAsync("/tmp/a\0b").AsTask()) is ArgumentException, "invalid paths throw");
    }

    // A second file system: /dev/shm is a tmpfs on Linux distributions and CI runners. The test treats a directory
    // there as the top of the mount, so the topdir trashes are created inside it.
    [Test, DisplayName("trash: an item on another file system goes to the trash at the top of its mount")]
    public async Task TopdirTrashAsync()
    {
        using var scratch = new Scratch();
        if (!Directory.Exists("/dev/shm") || LinuxTrash.MountPoint("/dev/shm") == LinuxTrash.MountPoint(scratch.Root))
        { Console.WriteLine("Skipped: /dev/shm is not a separate file system."); return; }
        string topdir = Path.Combine("/dev/shm", "runic-trash-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(topdir);
        try
        {
            string uid = GetEffectiveUserId().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var trash = new LinuxTrash(homeTrash: scratch.HomeTrash, sandboxed: false, mountPointOf: _ => topdir);
            Directory.CreateDirectory(Path.Combine(topdir, "docs"));
            File.WriteAllText(Path.Combine(topdir, "docs", "a.txt"), "a");
            Check(await trash.MoveToTrashAsync(Path.Combine(topdir, "docs", "a.txt")) is PlatformResult<PlatformUnit>.Success, "trashed to .Trash-$uid");
            string personal = Path.Combine(topdir, ".Trash-" + uid);
            Check(File.Exists(Path.Combine(personal, "files", "a.txt")), "in .Trash-$uid");
            Check(File.ReadAllText(Path.Combine(personal, "info", "a.txt.trashinfo")).Contains("\nPath=docs/a.txt\n", StringComparison.Ordinal),
                "the path is relative to the top directory");
            Check((File.GetUnixFileMode(personal) & (UnixFileMode)0x3F) == 0, ".Trash-$uid is private");
            Check(Directory.GetFiles(Path.Combine(scratch.HomeTrash, "info")).Length == 0, "nothing is left in the home trash");

            // An administrator's sticky .Trash is preferred.
            string shared = Path.Combine(topdir, ".Trash");
            Directory.CreateDirectory(shared);
            File.SetUnixFileMode(shared, (UnixFileMode)0x3FF); // 1777
            File.WriteAllText(Path.Combine(topdir, "docs", "b.txt"), "b");
            Check(await trash.MoveToTrashAsync(Path.Combine(topdir, "docs", "b.txt")) is PlatformResult<PlatformUnit>.Success
                && File.Exists(Path.Combine(shared, uid, "files", "b.txt")), "trashed to .Trash/$uid");

            // Without the sticky bit anyone could remove others' items: it is not used.
            File.SetUnixFileMode(shared, (UnixFileMode)0x1FF); // 0777
            File.WriteAllText(Path.Combine(topdir, "docs", "c.txt"), "c");
            Check(await trash.MoveToTrashAsync(Path.Combine(topdir, "docs", "c.txt")) is PlatformResult<PlatformUnit>.Success
                && File.Exists(Path.Combine(personal, "files", "c.txt")), "a .Trash without the sticky bit is skipped");

            // A .Trash-$uid that is a link could lead anywhere: no trash, and the item stays.
            Directory.Delete(shared, recursive: true);
            Directory.Delete(personal, recursive: true);
            string elsewhere = Path.Combine(topdir, "elsewhere");
            Directory.CreateDirectory(elsewhere);
            Directory.CreateSymbolicLink(personal, elsewhere);
            File.WriteAllText(Path.Combine(topdir, "docs", "d.txt"), "d");
            Check(await trash.MoveToTrashAsync(Path.Combine(topdir, "docs", "d.txt")) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
                && File.Exists(Path.Combine(topdir, "docs", "d.txt")) && Directory.GetFileSystemEntries(elsewhere).Length == 0, "a linked .Trash-$uid is not used");

            // No mount point known: no trash.
            var unknown = new LinuxTrash(homeTrash: scratch.HomeTrash, sandboxed: false, mountPointOf: _ => null);
            Check(await unknown.MoveToTrashAsync(Path.Combine(topdir, "docs", "d.txt")) is PlatformResult<PlatformUnit>.Unavailable
                && File.Exists(Path.Combine(topdir, "docs", "d.txt")), "an unknown mount keeps the item");
        }
        finally { Directory.Delete(topdir, recursive: true); }
    }

    [Test, DisplayName("trash: names, escapes and the mount table")]
    public void Helpers()
    {
        Check(LinuxTrash.UniqueName("a.txt", 1) == "a.txt" && LinuxTrash.UniqueName("a.tar.gz", 3) == "a.3.tar.gz"
            && LinuxTrash.UniqueName("README", 2) == "README.2" && LinuxTrash.UniqueName(".bashrc", 2) == ".bashrc.2", "unique names");
        Check(LinuxTrash.Escape("/home/u/a b/ä#?.txt") == "/home/u/a%20b/%C3%A4%23%3F.txt", "escape");
        Check(LinuxTrash.Unescape(@"/media/u/My\040Disk\134x") == @"/media/u/My Disk\x", "mountinfo escapes");
        string[] mounts = ["/", "/home", "/home/u/mnt", "/homework"];
        Check(LinuxTrash.MountPoint("/home/u/docs", mounts) == "/home" && LinuxTrash.MountPoint("/home/u/mnt/x", mounts) == "/home/u/mnt"
            && LinuxTrash.MountPoint("/homework/x", mounts) == "/homework" && LinuxTrash.MountPoint("/var", mounts) == "/"
            && LinuxTrash.MountPoint("/home", mounts) == "/home", "the longest containing mount point");
    }

    [Test, DisplayName("trash: providers and the unavailable trash")]
    public async Task ProvidersAsync()
    {
        Check(PortalPlatformProvider.CreateTrash() is LinuxTrash && new PortalApplication("org.example.App").CreateTrash() is LinuxTrash, "providers");
        var unavailable = DesktopTrash.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        Check(await unavailable.MoveToTrashAsync("/tmp/x") is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured }, "unavailable");
        Check(await Catch(() => unavailable.MoveToTrashAsync("x").AsTask()) is ArgumentException, "the unavailable trash checks the path");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Check(await Catch(() => unavailable.MoveToTrashAsync("/tmp/x", cancelled.Token).AsTask()) is OperationCanceledException, "cancelled");
        using var scratch = new Scratch();
        string item = scratch.File("kept.txt", "kept");
        Check(await Catch(() => new LinuxTrash(homeTrash: scratch.HomeTrash, sandboxed: false).MoveToTrashAsync(item, cancelled.Token).AsTask()) is OperationCanceledException
            && File.Exists(item), "a cancelled request moves nothing");
    }

    [Test, Explicit, Category("DBus"), DisplayName("trash: the Trash portal in a sandbox, trashed, refused, links, read-only files and missing")]
    public async Task PortalAsync()
    {
        using var scratch = new Scratch();
        using var service = new DBusConnection(DBusAddress.Session!);
        await service.ConnectAsync();
        var portal = new TrashPortal();
        service.AddMethodHandler(portal);
        var trash = new LinuxTrash(homeTrash: scratch.HomeTrash, sandboxed: true, destination: service.UniqueName!);

        string item = scratch.File("sandboxed.txt", "x");
        Check(await trash.MoveToTrashAsync(item).AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Success, "trashed");
        Check(portal.Received == item, $"the descriptor names the item: {portal.Received}");
        Check(File.Exists(item) && !Directory.Exists(scratch.HomeTrash), "the portal, not the provider, moves the item");

        Directory.CreateDirectory(Path.Combine(scratch.Root, "folder"));
        Check(await trash.MoveToTrashAsync(Path.Combine(scratch.Root, "folder")).AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Success
            && portal.Received == Path.Combine(scratch.Root, "folder"), "a directory is passed too");

        // The portal follows a link's descriptor to its target, so a link is not sent; nor is a file the sandbox cannot write.
        string link = Path.Combine(scratch.Root, "link");
        File.CreateSymbolicLink(link, item);
        portal.Received = null;
        Check(await trash.MoveToTrashAsync(link).AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Unavailable && portal.Received is null,
            "a link is not trashed through the portal");
        if (GetEffectiveUserId() != 0)
        {
            string readOnly = scratch.File("read-only.txt", "r");
            File.SetUnixFileMode(readOnly, UnixFileMode.UserRead);
            Check(await trash.MoveToTrashAsync(readOnly).AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }
                && portal.Received is null, "a file the application cannot write is not sent");
        }

        portal.Result = 0;
        Check(await trash.MoveToTrashAsync(item).AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Other }, "refused");
        Check(await trash.MoveToTrashAsync(Path.Combine(scratch.Root, "missing")).AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound },
            "a missing item is not sent");

        portal.Missing = true;
        Check(await trash.MoveToTrashAsync(item).AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "a portal without Trash");
        var absent = new LinuxTrash(homeTrash: scratch.HomeTrash, sandboxed: true, destination: "org.example.NoSuchPortal");
        Check(await absent.MoveToTrashAsync(item).AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Unavailable, "no portal");
    }

    // The real xdg-desktop-portal, which eng/ci/real-portal.sh runs in a container with a temporary home: the portal
    // itself trashes the item, into that home's trash or, on another file system, the trash at the top of the mount.
    [Test, Explicit, Category("RealPortalTrash"), DisplayName("trash: the real Trash portal moves a file and a directory to the trash")]
    public async Task RealPortalAsync()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Environment.GetEnvironmentVariable("RUNIC_TEST_REAL_PORTAL") is null || !home.StartsWith(Path.GetTempPath(), StringComparison.Ordinal))
            throw new InvalidOperationException("Run through eng/ci/real-portal.sh, whose home is a temporary directory.");
        string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } configured ? configured : Path.Combine(home, ".local", "share");
        using var scratch = new Scratch();
        string uid = GetEffectiveUserId().ToString(System.Globalization.CultureInfo.InvariantCulture);
        string[] trashes = [Path.Combine(data, "Trash"), Path.Combine(LinuxTrash.MountPoint(scratch.Root) ?? "/", ".Trash-" + uid)];
        string item = scratch.File("real portal.txt", "x");
        string folder = Path.Combine(scratch.Root, "real folder");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        var trash = new LinuxTrash(new PortalApplication(), sandboxed: true);

        // The portal names entries itself, such as "real portal.0.txt"; its info file records the original path.
        string? Trashed(string original) => trashes.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(Path.Combine(root, "info"), "*.trashinfo")
                .Where(info => File.ReadAllText(info).Contains("\nPath=" + LinuxTrash.Escape(original) + "\n", StringComparison.Ordinal))
                .Select(info => Path.Combine(root, "files", Path.GetFileNameWithoutExtension(info))))
            .FirstOrDefault();
        var result = await trash.MoveToTrashAsync(item).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        string? trashedFile = Trashed(item);
        Console.WriteLine($"Trash portal, file: {result}, to {trashedFile}");
        Check(result is PlatformResult<PlatformUnit>.Success && !File.Exists(item) && trashedFile is not null && File.ReadAllText(trashedFile) == "x",
            "the file is in a trash with its info");
        result = await trash.MoveToTrashAsync(folder).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        string? trashedFolder = Trashed(folder);
        Console.WriteLine($"Trash portal, directory: {result}, to {trashedFolder}");
        Check(result is PlatformResult<PlatformUnit>.Success && !Directory.Exists(folder) && trashedFolder is not null
            && Directory.Exists(Path.Combine(trashedFolder, "sub")), "the directory is in a trash with its info");
        Console.WriteLine("PASS the real Trash portal moved a file and a directory.");
    }

    private sealed class TrashPortal : IPathMethodHandler
    {
        public string Path => "/org/freedesktop/portal/desktop";
        public bool HandlesChildPaths => false;
        public uint Result = 1;
        public bool Missing;
        public string? Received;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (Missing || context.Request.InterfaceAsString != "org.freedesktop.portal.Trash" || context.Request.MemberAsString != "TrashFile")
            { context.ReplyUnknownMethodError(); return ValueTask.CompletedTask; }
            using (var handle = context.Request.GetBodyReader().ReadHandle<SafeFileHandle>())
                Received = new FileInfo("/proc/self/fd/" + handle!.DangerousGetHandle()).LinkTarget;
            using var reply = context.CreateReplyWriter("u");
            reply.WriteUInt32(Result);
            context.Reply(reply.CreateMessage());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Scratch : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "runic-trash-" + Guid.NewGuid().ToString("N")[..8]);
        internal string HomeTrash => Path.Combine(Root, "data", "Trash");
        internal Scratch() => Directory.CreateDirectory(Root);
        internal string File(string relative, string content)
        {
            string path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
            return path;
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private static async Task<Exception?> Catch(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
