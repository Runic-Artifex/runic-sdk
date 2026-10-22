using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Runic.Platform.Windows;

// The Recycle Bin's contents through its shell folder (FOLDERID_RecycleBinFolder), which spans every drive's bin. An
// item's filesystem path is its $R content file or directory; the $I file beside it records the original path, size
// and deletion time. Restoring runs the folder's own "undelete" verb without UI after checking that nothing is at the
// original path; emptying is SHEmptyRecycleBinW without confirmation, progress or sound. Every call runs on a
// single-threaded apartment thread of its own, as the shell's folders need.
[SupportedOSPlatform("windows8.0")]
internal static unsafe class WindowsRecycleBin
{
    private const int S_OK = 0, E_UNEXPECTED = unchecked((int)0x8000FFFF);
    private const uint CMF_NORMAL = 0, CMIC_MASK_NOASYNC = 0x100, CMIC_MASK_FLAG_NO_UI = 0x400, CMIC_MASK_UNICODE = 0x4000;
    private const uint SHERB_NOCONFIRMATION = 1, SHERB_NOPROGRESSUI = 2, SHERB_NOSOUND = 4;

    internal static PlatformResult<ImmutableArray<DesktopTrashItem>> List()
    {
        var items = ImmutableArray.CreateBuilder<DesktopTrashItem>();
        var result = Enumerate((_, path) =>
        {
            if (Describe(path) is { } item) items.Add(item);
            return false;
        });
        return result is { } failure ? new PlatformResult<ImmutableArray<DesktopTrashItem>>.Failed(failure.Code, failure.Diagnostic)
            : new PlatformResult<ImmutableArray<DesktopTrashItem>>.Success(items.ToImmutable());
    }

    internal static PlatformResult<PlatformUnit> Restore(string itemId)
    {
        PlatformResult<PlatformUnit>? outcome = null;
        var failure = Enumerate((child, path) =>
        {
            if (!string.Equals(path, itemId, StringComparison.OrdinalIgnoreCase)) return false;
            outcome = Undelete(child, path);
            return true;
        });
        if (failure is { } failed) return new PlatformResult<PlatformUnit>.Failed(failed.Code, failed.Diagnostic);
        return outcome ?? new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.NotFound);
    }

    internal static PlatformResult<PlatformUnit> Empty()
    {
        int hr = PInvoke.SHEmptyRecycleBin(default, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND).Value;
        // An already empty bin answers E_UNEXPECTED.
        if (hr == E_UNEXPECTED && IsEmpty()) hr = S_OK;
        return hr >= 0 ? new PlatformResult<PlatformUnit>.Success(default) : WindowsTrash.FromHResult(hr);
    }

    private static bool IsEmpty()
    {
        bool any = false;
        return Enumerate((_, _) => any = true) is null && !any;
    }

    // An item as its $I file records it; null for an entry without one, such as a bin of an older format.
    private static DesktopTrashItem? Describe(string content)
    {
        string? name = Path.GetFileName(content);
        if (name is null || !name.StartsWith("$R", StringComparison.OrdinalIgnoreCase)) return null;
        byte[] info;
        try { info = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(content)!, "$I" + name[2..])); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        if (RecycleInfo.Parse(info) is not { } record) return null;
        return new DesktopTrashItem(content, record.OriginalPath)
        {
            DeletedAt = record.DeletedAt,
            Size = record.Size,
            IsDirectory = Directory.Exists(content),
        };
    }

    private static PlatformResult<PlatformUnit> Undelete(IShellItem* child, string content)
    {
        if (Describe(content) is not { } item) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData);
        // The verb would ask before replacing, which no UI can; never replace.
        if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath)) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Conflict);
        Guid handler = PInvoke.BHID_SFUIObject, menuId = typeof(IContextMenu).GUID;
        void* created = null;
        int hr = child->BindToHandler(null, &handler, &menuId, &created).Value;
        if (hr < 0 || created is null) return WindowsTrash.FromHResult(hr < 0 ? hr : E_UNEXPECTED);
        var menu = (IContextMenu*)created;
        var popup = PInvoke.CreatePopupMenu();
        try
        {
            hr = menu->QueryContextMenu(popup, 0, 1, 0x7FFF, CMF_NORMAL).Value;
            if (hr < 0) return WindowsTrash.FromHResult(hr);
            fixed (byte* verb = "undelete\0"u8)
            fixed (char* verbW = "undelete")
            {
                var invoke = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = (uint)sizeof(CMINVOKECOMMANDINFOEX),
                    fMask = CMIC_MASK_FLAG_NO_UI | CMIC_MASK_NOASYNC | CMIC_MASK_UNICODE,
                    lpVerb = new PCSTR(verb),
                    lpVerbW = new PCWSTR(verbW),
                    nShow = (int)SHOW_WINDOW_CMD.SW_HIDE,
                };
                hr = menu->InvokeCommand((CMINVOKECOMMANDINFO*)&invoke).Value;
            }
        }
        finally
        {
            if (!popup.IsNull) PInvoke.DestroyMenu(popup);
            menu->Release();
        }
        bool restored = (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath)) && !File.Exists(content) && !Directory.Exists(content);
        if (restored) return new PlatformResult<PlatformUnit>.Success(default);
        return WindowsTrash.FromHResult(hr < 0 ? hr : E_UNEXPECTED);
    }

    private delegate bool Visitor(IShellItem* child, string path);

    // Calls visit with each item of the Recycle Bin folder and its filesystem path until it returns true; the failure
    // to open the folder, if any.
    private static PlatformResult<PlatformUnit>.Failed? Enumerate(Visitor visit)
    {
        Guid folder = PInvoke.FOLDERID_RecycleBinFolder, itemId = typeof(IShellItem).GUID;
        void* created = null;
        int hr = PInvoke.SHGetKnownFolderItem(&folder, KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, default, &itemId, &created).Value;
        if (hr < 0 || created is null) return Failure(hr);
        var root = (IShellItem*)created;
        IEnumShellItems* children = null;
        try
        {
            Guid handlerId = PInvoke.BHID_EnumItems, enumerationId = typeof(IEnumShellItems).GUID;
            void* enumeration = null;
            hr = root->BindToHandler(null, &handlerId, &enumerationId, &enumeration).Value;
            if (hr < 0 || enumeration is null) return Failure(hr);
            children = (IEnumShellItems*)enumeration;
            while (true)
            {
                IShellItem* child = null;
                uint fetched = 0;
                if (children->Next(1, &child, &fetched).Value != S_OK || fetched != 1 || child is null) return null;
                try
                {
                    if (FileSystemPath(child) is { } path && visit(child, path)) return null;
                }
                finally { child->Release(); }
            }
        }
        finally
        {
            if (children is not null) children->Release();
            root->Release();
        }
    }

    private static PlatformResult<PlatformUnit>.Failed Failure(int hr) =>
        (PlatformResult<PlatformUnit>.Failed)WindowsTrash.FromHResult(hr < 0 ? hr : E_UNEXPECTED);

    private static string? FileSystemPath(IShellItem* item)
    {
        PWSTR path = default;
        if (item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &path).Value < 0) return null;
        try { return Marshal.PtrToStringUni((nint)path.Value); }
        finally { Marshal.FreeCoTaskMem((nint)path.Value); }
    }
}

// The $I file of a Recycle Bin item: a version (1 before Windows 10, 2 since), the item's size and its deletion time
// as a FILETIME, then the original path: 260 UTF-16 characters in version 1, a length in characters (with the
// terminator) and the path in version 2.
internal readonly record struct RecycleInfo(string OriginalPath, long? Size, DateTimeOffset? DeletedAt)
{
    private const int Header = 24, FixedPath = 260;

    internal static RecycleInfo? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Header) return null;
        long version = BinaryPrimitives.ReadInt64LittleEndian(data);
        long size = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
        long time = BinaryPrimitives.ReadInt64LittleEndian(data[16..]);
        ReadOnlySpan<byte> path;
        if (version == 1)
        {
            if (data.Length < Header + FixedPath * 2) return null;
            path = data.Slice(Header, FixedPath * 2);
        }
        else if (version == 2)
        {
            if (data.Length < Header + 4) return null;
            int length = BinaryPrimitives.ReadInt32LittleEndian(data[Header..]);
            if (length <= 0 || length > 32_768 || data.Length < Header + 4 + length * 2) return null;
            path = data.Slice(Header + 4, length * 2);
        }
        else return null;
        string original = MemoryMarshal.Cast<byte, char>(path).ToString();
        int end = original.IndexOf('\0');
        if (end >= 0) original = original[..end];
        if (!IsFullyQualified(original)) return null;
        DateTimeOffset? deleted = time > 0 && time < DateTime.MaxValue.ToFileTimeUtc() ? DateTimeOffset.FromFileTime(time).ToUniversalTime() : null;
        return new RecycleInfo(original, size >= 0 ? size : null, deleted);
    }

    // A drive path (C:\…) or a UNC path (\\server\share\…), checked as Windows does whatever the current platform.
    private static bool IsFullyQualified(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
        || path.Length >= 3 && path[0] == '\\' && path[1] == '\\' && path[2] != '\\';
}
