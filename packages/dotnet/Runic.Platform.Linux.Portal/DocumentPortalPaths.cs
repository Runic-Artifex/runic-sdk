using System.Runtime.InteropServices;
using System.Text;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// Classifies a selected file as a document-portal view or an ordinary host path.
// An unsandboxed caller, or a sandbox that already reaches the path, receives the
// real host path. Otherwise the portal returns <mount>/<doc-id>/<name> (inside
// Flatpak a bind of <mount>/by-app/<app-id>, also visible as /run/flatpak/doc).
// The mount comes from Documents.GetMountPoint, never from a guessed
// XDG_RUNTIME_DIR, and the directory must actually be the FUSE filesystem.
internal sealed partial class DocumentPortalPaths(Func<CancellationToken, ValueTask<string?>>? mountPoint = null, Func<string, bool>? isFuse = null)
{
    internal const string FlatpakMount = "/run/flatpak/doc";
    private const long FuseSuperMagic = 0x65735546;
    internal static DocumentPortalPaths Session { get; } = new();

    internal async ValueTask<NativeFileLocation> ClassifyAsync(string path, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        // Most selections are host paths: avoid the D-Bus call unless the directory is FUSE.
        if (directory is null || !(isFuse ?? IsFuse)(directory)) return NativeFileLocation.Host;
        if (IsWithin(directory, FlatpakMount)) return NativeFileLocation.DocumentPortal;
        string? mount = await (mountPoint ?? QueryMountPointAsync)(cancellationToken).ConfigureAwait(false);
        return mount is not null && IsWithin(directory, mount) ? NativeFileLocation.DocumentPortal : NativeFileLocation.Host;
    }

    // Documents are always below the mount, in <doc-id> or by-app/<app-id>/<doc-id>.
    private static bool IsWithin(string directory, string mount)
    {
        mount = mount.TrimEnd('/');
        return mount.Length > 0 && directory.Length > mount.Length + 1
            && directory.StartsWith(mount, StringComparison.Ordinal) && directory[mount.Length] == '/';
    }

    internal static unsafe bool IsFuse(string directory)
    {
        // struct statfs starts with f_type (a C long on Linux); 256 bytes covers every ABI.
        byte* buffer = stackalloc byte[256];
        try { return StatFs(directory, buffer) == 0 && (*(nint*)buffer & 0xFFFFFFFF) == FuseSuperMagic; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
    [LibraryImport("libc", EntryPoint = "statfs", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int StatFs(string path, byte* buffer);

    // The Documents portal is reachable from inside the sandbox. A live document
    // view implies a running portal, so never activate one: a new instance would
    // take over the session's mount. Any failure means the path cannot be
    // confirmed as a document; it is then treated as a host path.
    internal static async ValueTask<string?> QueryMountPointAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = (await PortalBus.Session(null, null).ConnectAsync(cancellationToken).ConfigureAwait(false)).Connection;
            var reply = await connection.CallMethodAsync(new DBusMethodCall("org.freedesktop.portal.Documents", new DBusObjectPath("/org/freedesktop/portal/documents"),
                "org.freedesktop.portal.Documents", "GetMountPoint") { Options = DBusCallOptions.NoAutoStart }, cancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (reply is not [{ Signature.Value: "ay" } path]) return null;
            var bytes = path.GetByteArray().AsSpan();
            int length = bytes.IndexOf((byte)0);
            return new UTF8Encoding(false, true).GetString(length < 0 ? bytes : bytes[..length]);
        }
        catch (Exception error) when (error is DBusException or TimeoutException or IOException or DecoderFallbackException or NativeBackendUnavailableException) { return null; }
    }
}
