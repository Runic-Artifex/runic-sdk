using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.Shell.PropertiesSystem;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Runic.Platform.Windows;

// The shell's association database through raw COM vtables (CsWin32, allowMarshaling: false), as the trash and the
// file dialogs call the shell. Every method runs on a single-threaded apartment (WindowsSta) and releases what it
// acquires before it returns.
[SupportedOSPlatform("windows8.0")]
internal static unsafe class WindowsShellAssociations
{
    internal readonly record struct Handler(string Id, string Name);
    internal readonly record struct AppsFolderEntry(string Id, string Name, string ParsingName);

    private const int S_OK = 0;

    // AssocQueryString for an association: the string, or null when the association has none.
    internal static string? Query(Association association, ASSOCSTR kind)
    {
        ASSOCF flags = association.Kind switch
        {
            AssociationKind.Extension => ASSOCF.ASSOCF_INIT_IGNOREUNKNOWN,
            AssociationKind.Protocol => ASSOCF.ASSOCF_IS_PROTOCOL | ASSOCF.ASSOCF_INIT_IGNOREUNKNOWN,
            _ => ASSOCF.ASSOCF_NONE,
        };
        uint length = 0;
        fixed (char* name = association.Name)
        {
            // Without a buffer the call reports the length, including the terminator, with S_FALSE.
            if (PInvoke.AssocQueryString(flags, kind, name, default, default, &length).Value < 0 || length is 0 or > 32768) return null;
            char[] buffer = new char[length];
            fixed (char* output = buffer)
            {
                if (PInvoke.AssocQueryString(flags | ASSOCF.ASSOCF_NOTRUNCATE, kind, name, default, output, &length).Value < 0) return null;
            }
            string value = new string(buffer, 0, (int)Math.Min(length, (uint)buffer.Length)).TrimEnd('\0');
            return value.Length == 0 ? null : value;
        }
    }

    // The application that opens the association by default: its executable or, for a packaged application, its
    // AppUserModelID, with its name.
    internal static Handler? Default(Association association)
    {
        string? id = Query(association, ASSOCSTR.ASSOCSTR_EXECUTABLE) ?? Query(association, ASSOCSTR.ASSOCSTR_APPID);
        if (id is null) return null;
        string name = Query(association, ASSOCSTR.ASSOCSTR_FRIENDLYAPPNAME) ?? Path.GetFileNameWithoutExtension(id);
        return new Handler(id, name.Length == 0 ? id : name);
    }

    // The handlers SHAssocEnumHandlers lists for the association. With a match, the matching handler is returned
    // AddRef'd in `kept` for Invoke; the caller releases it.
    internal static List<Handler> Handlers(Association association, bool recommended, string? match, out IAssocHandler* kept)
    {
        kept = null;
        var handlers = new List<Handler>();
        IEnumAssocHandlers* handlerEnumeration = null;
        int hr;
        fixed (char* name = association.Name)
        {
            if (association.Kind == AssociationKind.Protocol)
            {
                Guid iid = typeof(IEnumAssocHandlers).GUID;
                void* created = null;
                hr = PInvoke.SHAssocEnumHandlersForProtocolByApplication(name, &iid, &created).Value;
                handlerEnumeration = (IEnumAssocHandlers*)created;
            }
            else
            {
                hr = PInvoke.SHAssocEnumHandlers(name, recommended ? ASSOC_FILTER.ASSOC_FILTER_RECOMMENDED : ASSOC_FILTER.ASSOC_FILTER_NONE,
                    &handlerEnumeration).Value;
            }
        }
        if (hr < 0 || handlerEnumeration is null) return handlers;
        try
        {
            while (true)
            {
                IAssocHandler* handler = null;
                uint fetched = 0;
                if (handlerEnumeration->Next(1, &handler, &fetched).Value != S_OK || fetched != 1 || handler is null) break;
                try
                {
                    PWSTR value = default;
                    string? id = handler->GetName(&value).Value >= 0 ? Take(value) : null;
                    value = default;
                    string? display = handler->GetUIName(&value).Value >= 0 ? Take(value) : null;
                    if (string.IsNullOrEmpty(id) || id.Contains('\0') || handlers.Exists(existing => SameId(existing.Id, id))) continue;
                    handlers.Add(new Handler(id, string.IsNullOrEmpty(display) || display.Contains('\0') ? Path.GetFileNameWithoutExtension(id) : display));
                    if (match is not null && kept is null && SameId(id, match))
                    {
                        handler->AddRef();
                        kept = handler;
                    }
                }
                finally { handler->Release(); }
            }
        }
        finally { handlerEnumeration->Release(); }
        return handlers;
    }

    internal static bool SameId(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    // Hands the items to the handler as the shell's own Open With does: a data object over their parsed shell items.
    internal static int Invoke(IAssocHandler* handler, IReadOnlyList<string> items)
    {
        var parsed = new ITEMIDLIST*[items.Count];
        IShellItemArray* array = null;
        void* dataObject = null;
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                ITEMIDLIST* item = null;
                fixed (char* text = items[i])
                {
                    int hr = PInvoke.SHParseDisplayName(text, null, &item, 0, null).Value;
                    if (hr < 0) return hr;
                }
                parsed[i] = item;
            }
            fixed (ITEMIDLIST** list = parsed)
            {
                int hr = PInvoke.SHCreateShellItemArrayFromIDLists((uint)parsed.Length, list, &array).Value;
                if (hr < 0) return hr;
            }
            Guid handlerId = PInvoke.BHID_DataObject, iid = typeof(IDataObject).GUID;
            int bound = array->BindToHandler(null, &handlerId, &iid, &dataObject).Value;
            if (bound < 0) return bound;
            return handler->Invoke((IDataObject*)dataObject).Value;
        }
        finally
        {
            if (dataObject is not null) ((IUnknown*)dataObject)->Release();
            if (array is not null) array->Release();
            foreach (var item in parsed) if (item is not null) Marshal.FreeCoTaskMem((nint)item);
        }
    }

    // The Start menu's applications (shell:AppsFolder). A desktop application is identified by its executable, as
    // the association handlers are; a packaged application by its AppUserModelID. Shortcuts to documents and web
    // pages are not applications.
    internal static List<AppsFolderEntry>? AppsFolder()
    {
        Guid folder = PInvoke.FOLDERID_AppsFolder, itemId = typeof(IShellItem).GUID;
        void* created = null;
        if (PInvoke.SHGetKnownFolderItem(&folder, KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, default, &itemId, &created).Value < 0 || created is null) return null;
        var root = (IShellItem*)created;
        IEnumShellItems* children = null;
        try
        {
            Guid handlerId = PInvoke.BHID_EnumItems, enumerationId = typeof(IEnumShellItems).GUID;
            void* enumeration = null;
            if (root->BindToHandler(null, &handlerId, &enumerationId, &enumeration).Value < 0 || enumeration is null) return null;
            children = (IEnumShellItems*)enumeration;
            var entries = new List<AppsFolderEntry>();
            Guid item2Id = typeof(IShellItem2).GUID;
            PROPERTYKEY target = PInvoke.PKEY_Link_TargetParsingPath;
            while (true)
            {
                IShellItem* child = null;
                uint fetched = 0;
                if (children->Next(1, &child, &fetched).Value != S_OK || fetched != 1 || child is null) break;
                try
                {
                    string? name = DisplayName(child, SIGDN.SIGDN_NORMALDISPLAY);
                    string? parsing = DisplayName(child, SIGDN.SIGDN_PARENTRELATIVEPARSING);
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parsing) || name.Contains('\0') || parsing.Contains('\0')) continue;
                    string? path = null;
                    void* item2 = null;
                    if (child->QueryInterface(&item2Id, &item2).Value >= 0 && item2 is not null)
                    {
                        PWSTR value = default;
                        if (((IShellItem2*)item2)->GetString(&target, &value).Value >= 0) path = Take(value);
                        ((IShellItem2*)item2)->Release();
                    }
                    string? id = Identify(path, parsing);
                    if (id is not null && !entries.Exists(entry => SameId(entry.Id, id))) entries.Add(new AppsFolderEntry(id, name, parsing));
                }
                finally { child->Release(); }
            }
            return entries;
        }
        finally
        {
            if (children is not null) children->Release();
            root->Release();
        }
    }

    // The entry with the identifier, or null. (List.Find would return an empty entry for a struct.)
    internal static AppsFolderEntry? FindEntry(List<AppsFolderEntry>? entries, string id)
    {
        int index = entries?.FindIndex(entry => SameId(entry.Id, id)) ?? -1;
        return index < 0 ? null : entries![index];
    }

    // An entry's identifier: the executable a desktop shortcut targets, else the AppUserModelID; null for a shortcut to
    // anything other than a program.
    internal static string? Identify(string? target, string parsingName)
    {
        if (string.IsNullOrEmpty(target)) return parsingName;
        return Path.IsPathFullyQualified(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    internal static bool StartAppsFolderEntry(string parsingName, out int status)
    {
        fixed (char* file = @"shell:AppsFolder\" + parsingName)
        {
            // Raw overload: the HINSTANCE is a legacy status code, as in WindowsFileLauncher.
            status = (int)(nint)PInvoke.ShellExecute(default, default, file, default, default, SHOW_WINDOW_CMD.SW_SHOWNORMAL).Value;
            return status > 32;
        }
    }

    // An icon from a file's icon resources (an executable, a DLL, an .ico), at the size requested, as PNG.
    internal static byte[]? ExtractIcon(string file, int index, int pixels)
    {
        HICON icon = default;
        fixed (char* path = file)
        {
            if (PInvoke.SHDefExtractIcon(path, index, 0, &icon, null, (uint)pixels).Value != S_OK || icon.IsNull) return null;
        }
        try { return IconPng(icon); }
        finally { PInvoke.DestroyIcon(icon); }
    }

    // The icon the shell shows for an item, such as a packaged application in shell:AppsFolder, as PNG.
    internal static byte[]? ShellItemIcon(string parsingName, int pixels)
    {
        Guid factoryId = typeof(IShellItemImageFactory).GUID;
        void* created = null;
        fixed (char* name = parsingName)
        {
            if (PInvoke.SHCreateItemFromParsingName(name, null, &factoryId, &created).Value < 0 || created is null) return null;
        }
        return FactoryIcon((IShellItemImageFactory*)created, pixels);
    }

    // The icon Explorer shows for a file with the extension, from a simple ID list of a name that need not exist. It
    // covers types whose DefaultIcon is not an icon resource, such as those a packaged application opens.
    internal static byte[]? ExtensionIcon(string extension, int pixels)
    {
        ITEMIDLIST* list;
        fixed (char* path = Path.Combine(Path.GetTempPath(), "runic-icon" + extension)) list = PInvoke.SHSimpleIDListFromPath(path);
        if (list is null) return null;
        try
        {
            Guid factoryId = typeof(IShellItemImageFactory).GUID;
            void* created = null;
            if (PInvoke.SHCreateItemFromIDList(list, &factoryId, &created).Value < 0 || created is null) return null;
            return FactoryIcon((IShellItemImageFactory*)created, pixels);
        }
        finally { Marshal.FreeCoTaskMem((nint)list); }
    }

    // Releases the factory.
    private static byte[]? FactoryIcon(IShellItemImageFactory* factory, int pixels)
    {
        HBITMAP bitmap = default;
        try
        {
            if (factory->GetImage(new SIZE(pixels, pixels), SIIGBF.SIIGBF_ICONONLY, &bitmap).Value < 0 || bitmap.IsNull) return null;
            using var dc = new MemoryDc();
            var pixelsRead = Pixels(dc.Handle, bitmap, out int width, out int height);
            if (pixelsRead is null) return null;
            if (!WindowsIconImage.HasAlpha(pixelsRead)) for (int i = 3; i < pixelsRead.Length; i += 4) pixelsRead[i] = 255;
            else WindowsIconImage.Straighten(pixelsRead);
            return WindowsIconImage.EncodePng(pixelsRead, width, height);
        }
        finally
        {
            if (!bitmap.IsNull) PInvoke.DeleteObject(bitmap);
            factory->Release();
        }
    }

    private static byte[]? IconPng(HICON icon)
    {
        ICONINFO info;
        if (!PInvoke.GetIconInfo(icon, &info)) return null;
        try
        {
            // A monochrome icon has no colour bitmap; its mask holds both halves.
            if (info.hbmColor.IsNull) return null;
            using var dc = new MemoryDc();
            var color = Pixels(dc.Handle, info.hbmColor, out int width, out int height);
            if (color is null) return null;
            if (!WindowsIconImage.HasAlpha(color))
            {
                int maskWidth = 0, maskHeight = 0;
                var mask = info.hbmMask.IsNull ? null : Pixels(dc.Handle, info.hbmMask, out maskWidth, out maskHeight);
                if (mask is null || maskWidth != width || maskHeight != height) for (int i = 3; i < color.Length; i += 4) color[i] = 255;
                else WindowsIconImage.ApplyMask(color, mask);
            }
            return WindowsIconImage.EncodePng(color, width, height);
        }
        finally
        {
            if (!info.hbmColor.IsNull) PInvoke.DeleteObject(info.hbmColor);
            if (!info.hbmMask.IsNull) PInvoke.DeleteObject(info.hbmMask);
        }
    }

    // A bitmap's pixels as 32-bit BGRA, top-down. GetDIBits converts other formats, including the 1-bit icon mask.
    private static byte[]? Pixels(HDC dc, HBITMAP bitmap, out int width, out int height)
    {
        width = height = 0;
        // Room for the header and the largest colour table GetDIBits may write when it reports the format.
        byte* buffer = stackalloc byte[sizeof(BITMAPINFOHEADER) + 256 * 4];
        new Span<byte>(buffer, sizeof(BITMAPINFOHEADER) + 256 * 4).Clear();
        var header = (BITMAPINFOHEADER*)buffer;
        header->biSize = (uint)sizeof(BITMAPINFOHEADER);
        if (PInvoke.GetDIBits(dc, bitmap, 0, 0, null, (BITMAPINFO*)buffer, DIB_USAGE.DIB_RGB_COLORS) == 0) return null;
        width = header->biWidth;
        height = Math.Abs(header->biHeight);
        if (width is <= 0 or > 4096 || height is <= 0 or > 4096) return null;
        new Span<byte>(buffer, sizeof(BITMAPINFOHEADER) + 256 * 4).Clear();
        header->biSize = (uint)sizeof(BITMAPINFOHEADER);
        header->biWidth = width;
        header->biHeight = -height; // Top-down.
        header->biPlanes = 1;
        header->biBitCount = 32;
        header->biCompression = 0; // BI_RGB
        var pixels = new byte[width * height * 4];
        fixed (byte* bits = pixels)
        {
            if (PInvoke.GetDIBits(dc, bitmap, 0, (uint)height, bits, (BITMAPINFO*)buffer, DIB_USAGE.DIB_RGB_COLORS) != height) return null;
        }
        return pixels;
    }

    // urlmon's content sniffing, as Internet Explorer and the shell's attachment manager detect a type: images, HTML,
    // XML, PDF, archives, text or binary. FMFD_RETURNUPDATEDIMGMIMES returns image/png and image/jpeg rather than the
    // legacy image/x-png and image/pjpeg.
    internal static string? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return null;
        PWSTR mime = default;
        fixed (byte* bytes = data)
        {
            if (PInvoke.FindMimeFromData(null, default, bytes, (uint)data.Length, default, 0x20, &mime, 0).Value < 0) return null;
        }
        return Take(mime);
    }

    private static string? DisplayName(IShellItem* item, SIGDN kind)
    {
        PWSTR value = default;
        return item->GetDisplayName(kind, &value).Value >= 0 ? Take(value) : null;
    }

    // Copies and frees a string the shell allocated with CoTaskMemAlloc.
    private static string? Take(PWSTR value)
    {
        if (value.Value is null) return null;
        try { return new string(value.Value); }
        finally { Marshal.FreeCoTaskMem((nint)value.Value); }
    }

    private readonly struct MemoryDc : IDisposable
    {
        internal readonly HDC Handle;
        public MemoryDc() => Handle = PInvoke.CreateCompatibleDC(default);
        public void Dispose() { if (!Handle.IsNull) PInvoke.DeleteDC(Handle); }
    }
}
