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

    // The longest a launch keeps its apartment alive for the shell's asynchronous execution.
    private const int LaunchCompletionMilliseconds = 30_000;

    // What the last launch saw, for the native tests' log: the HRESULT, and how long the apartment stayed for the shell.
    internal static volatile string? LastLaunchTrace;

    // Invoke, holding a thread reference (SHSetThreadRef) that the shell's asynchronous execution takes while it starts
    // the handler, and pumping messages while the shell still holds the thread reference or the items' data object,
    // which lives in this apartment: the apartment must outlive the launch, as Explorer's do.
    internal static int InvokeAndWait(IAssocHandler* handler, IReadOnlyList<string> items)
    {
        int result = InvokeAndWaitOnce(handler, items);
        if (result != SelectionUnsupported) return result;
        // The handler cannot take the items together: each one is invoked on its own, as the shell's Open With does.
        foreach (string item in items)
        {
            result = InvokeAndWaitOnce(handler, [item]);
            if (result < 0) return result;
        }
        return S_OK;
    }

    // IAssocHandlerInvoker::SupportsSelection said no; nothing started.
    private const int SelectionUnsupported = 1;

    private static int InvokeAndWaitOnce(IAssocHandler* handler, IReadOnlyList<string> items)
    {
        // The count outlives a launch that does not finish in time, so it is freed only once it reaches zero.
        int* count = (int*)NativeMemory.AllocZeroed(sizeof(int));
        IUnknown* threadReference = null;
        bool tracked = PInvoke.SHCreateThreadRef(count, &threadReference).Value >= 0 && threadReference is not null
            && PInvoke.SHSetThreadRef(threadReference).Value >= 0;
        IUnknown* dataObject = null;
        int hr;
        try { hr = Invoke(handler, items, out dataObject); }
        finally
        {
            if (tracked) PInvoke.SHSetThreadRef(null);
            if (threadReference is not null) threadReference->Release();
        }
        long started = Environment.TickCount64, deadline = started + LaunchCompletionMilliseconds;
        int threadReferences = tracked ? Volatile.Read(ref *count) : 0;
        uint itemReferences = OtherReferences(dataObject);
        try
        {
            while (hr == S_OK && ((tracked && Volatile.Read(ref *count) > 0) || OtherReferences(dataObject) > 0) && Environment.TickCount64 < deadline)
            {
                PInvoke.MsgWaitForMultipleObjectsEx(0, null, 50, QUEUE_STATUS_FLAGS.QS_ALLINPUT, MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
                MSG message;
                while (PInvoke.PeekMessage(&message, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
                {
                    PInvoke.TranslateMessage(&message);
                    PInvoke.DispatchMessage(&message);
                }
            }
        }
        finally { if (dataObject is not null) dataObject->Release(); }
        LastLaunchTrace = $"Invoke 0x{hr:X8}; thread reference {(tracked ? $"held {threadReferences} after the call" : "unavailable")}; " +
            $"the data object had {itemReferences} other references; the apartment stayed {Environment.TickCount64 - started} ms";
        if (Volatile.Read(ref *count) <= 0) NativeMemory.Free(count);
        return hr;
    }

    // The references to an object besides the caller's own. Release returns the new count, which in-process objects
    // report exactly.
    private static uint OtherReferences(IUnknown* value)
    {
        if (value is null) return 0;
        value->AddRef();
        uint count = value->Release();
        return count > 0 ? count - 1 : 0;
    }

    // Hands the items to the handler as the shell's own Open With does: a data object over their parsed shell items,
    // which the caller releases. One item goes to IAssocHandler::Invoke; a selection to the invoker CreateInvoker
    // returns, if it supports the selection (the documented way for several items), else SelectionUnsupported.
    private static int Invoke(IAssocHandler* handler, IReadOnlyList<string> items, out IUnknown* dataObject)
    {
        dataObject = null;
        var parsed = new ITEMIDLIST*[items.Count];
        IShellItemArray* array = null;
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
            void* bound = null;
            int result = array->BindToHandler(null, &handlerId, &iid, &bound).Value;
            if (result < 0 || bound is null) return result < 0 ? result : unchecked((int)0x80004005);
            dataObject = (IUnknown*)bound;
            if (items.Count == 1) return handler->Invoke((IDataObject*)bound).Value;
            IAssocHandlerInvoker* invoker = null;
            result = handler->CreateInvoker((IDataObject*)bound, &invoker).Value;
            if (result < 0 || invoker is null) return result < 0 ? result : unchecked((int)0x80004005);
            try { return invoker->SupportsSelection().Value == S_OK ? invoker->Invoke().Value : SelectionUnsupported; }
            finally { invoker->Release(); }
        }
        finally
        {
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
        try { return WindowsPng.Encode(default, icon, out var png, out _, out _, out _) >= 0 ? png : null; }
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
            return WindowsPng.Encode(bitmap, default, out var png, out _, out _, out _) >= 0 ? png : null;
        }
        finally
        {
            if (!bitmap.IsNull) PInvoke.DeleteObject(bitmap);
            factory->Release();
        }
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
}
