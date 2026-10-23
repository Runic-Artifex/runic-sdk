using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// A drag out of a Win32 window: the shell's own data object for the paths (the shell ID list and what Explorer asks
// for), with CF_HDROP, the text, the first URI and the preferred effect added, dragged through SHDoDragDrop with the
// shell's drag image and Runic's drop source. It runs on the window's STA thread while the primary button is held: the
// WebView2 child shares the window's input state, so the OLE loop takes the capture over from the browser process
// after the page cancelled its own dragstart.
internal static partial class WindowsDragSource
{
    private const int DragDropCancel = 0x00040101; // DRAGDROP_S_CANCEL
    private const ushort UnicodeText = 13; // CF_UNICODETEXT
    private const ushort FileDrop = 15; // CF_HDROP
    private const uint ContentAspect = 1; // DVASPECT_CONTENT
    private const uint HGlobal = 1; // TYMED_HGLOBAL
    private const uint MoveableMemory = 0x0002; // GMEM_MOVEABLE

    // On the window's STA thread. None without a held primary button, when cancelled or dropped nowhere.
    internal static unsafe DesktopDragEffects Run(nint window, DesktopDragData data)
    {
        if (!PrimaryButtonDown()) return DesktopDragEffects.None;
        // The window thread is a COM STA; the drag loop needs OLE as well.
        Marshal.ThrowExceptionForHR(OleInitialize(0));
        try
        {
            var dataObject = CreateDataObject(data);
            try
            {
                var source = new DropSource();
                var sourcePointer = (nint)ComInterfaceMarshaller<IDropSource>.ConvertToUnmanaged(source);
                int hr;
                uint effect;
                try { hr = SHDoDragDrop(window, dataObject, sourcePointer, ToDropEffect(data.AllowedEffects), out effect); }
                finally { ComInterfaceMarshaller<IDropSource>.Free((void*)sourcePointer); }
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                if (hr == DragDropCancel) return DesktopDragEffects.None;
                var performed = FromDropEffect(effect);
                // An optimized move (Explorer on the same volume) reports no effect and moves the files itself; the
                // target records the move it did as the logical performed effect.
                if (performed == DesktopDragEffects.None && ReadEffect(dataObject, "Logical Performed DropEffect") is { } logical)
                    performed = FromDropEffect(logical);
                return DesktopDragRules.Single(performed, data.AllowedEffects);
            }
            finally
            {
                Release(dataObject);
            }
        }
        finally
        {
            OleUninitialize();
        }
    }

    internal static bool PrimaryButtonDown()
    {
        var key = GetSystemMetrics(23) != 0 ? 0x02 : 0x01; // SM_SWAPBUTTON: VK_RBUTTON, else VK_LBUTTON
        return (GetAsyncKeyState(key) & 0x8000) != 0;
    }

    // The keyboard focus is in the WebView2 browser process, whose thread receives the Escape key message, so the OLE
    // loop never reports it: the source reads Escape's state itself (down now, or pressed since the last read). The drag drops once no mouse button is held.
    [GeneratedComInterface, Guid("00000121-0000-0000-C000-000000000046")]
    internal partial interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(int escapePressed, uint keyState);
        [PreserveSig] int GiveFeedback(uint effect);
    }

    [GeneratedComClass]
    internal sealed partial class DropSource : IDropSource
    {
        public int QueryContinueDrag(int escapePressed, uint keyState) => Continue(escapePressed != 0 || (GetAsyncKeyState(0x1B) & 0x8001) != 0, keyState);

        public int GiveFeedback(uint effect) => 0x00040102; // DRAGDROP_S_USEDEFAULTCURSORS
    }

    // DRAGDROP_S_CANCEL on Escape, DRAGDROP_S_DROP once neither MK_LBUTTON nor MK_RBUTTON is held, else S_OK.
    internal static int Continue(bool escape, uint keyState) =>
        escape ? DragDropCancel : (keyState & 0x3) == 0 ? 0x00040100 : 0;

    internal static uint ToDropEffect(DesktopDragEffects effects) => (uint)effects; // DROPEFFECT_COPY 1, MOVE 2, LINK 4

    internal static DesktopDragEffects FromDropEffect(uint effect) =>
        (DesktopDragEffects)(effect & 7);

    // The text a drag offers as CF_UNICODETEXT: the text, or else the URIs, one per line.
    internal static string? PlainText(DesktopDragData data) =>
        data.Text ?? (data.Uris.IsDefaultOrEmpty ? null : string.Join("\r\n", data.Uris.Select(static uri => uri.AbsoluteUri)));

    private static unsafe nint CreateDataObject(DesktopDragData data)
    {
        var paths = data.Paths.IsDefault ? [] : data.Paths;
        var pidls = new nint[paths.Length];
        try
        {
            for (var index = 0; index < paths.Length; index++)
                Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[index], 0, out pidls[index], 0, out _));
            var iid = new Guid("0000010e-0000-0000-C000-000000000046"); // IID_IDataObject
            nint dataObject;
            // Absolute ID lists relative to the desktop: the items may live in different folders.
            fixed (nint* items = pidls)
                Marshal.ThrowExceptionForHR(SHCreateDataObject(0, (uint)pidls.Length, pidls.Length == 0 ? null : items, 0, &iid, &dataObject));
            try
            {
                // The shell data object made without a parent folder offers only the ID list; drop targets such as
                // WPF and Chromium read CF_HDROP, so it is set explicitly.
                if (!paths.IsEmpty) SetBytes(dataObject, FileDrop, DropFiles(paths));
                if (PlainText(data) is { } text) SetString(dataObject, UnicodeText, text);
                if (!data.Uris.IsDefaultOrEmpty) SetString(dataObject, Register("UniformResourceLocatorW"), data.Uris[0].AbsoluteUri);
                var preferred = data.AllowedEffects.HasFlag(DesktopDragEffects.Copy) ? DesktopDragEffects.Copy
                    : data.AllowedEffects.HasFlag(DesktopDragEffects.Move) ? DesktopDragEffects.Move
                    : DesktopDragEffects.Link;
                SetBytes(dataObject, Register("Preferred DropEffect"), BitConverter.GetBytes(ToDropEffect(preferred)));
                return dataObject;
            }
            catch
            {
                Release(dataObject);
                throw;
            }
        }
        finally
        {
            foreach (var pidl in pidls) if (pidl != 0) Marshal.FreeCoTaskMem(pidl);
        }
    }

    // CF_HDROP: a DROPFILES header (pFiles = 20, fWide = 1) and the paths as NUL-terminated UTF-16, ending with an
    // empty one.
    internal static byte[] DropFiles(IReadOnlyList<string> paths)
    {
        var text = string.Concat(paths.Select(static path => path + '\0')) + '\0';
        var bytes = new byte[20 + text.Length * 2];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 20);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), 1);
        System.Text.Encoding.Unicode.GetBytes(text, bytes.AsSpan(20));
        return bytes;
    }

    private static ushort Register(string format)
    {
        var id = RegisterClipboardFormat(format);
        return id == 0 ? throw new InvalidOperationException($"The clipboard format {format} could not be registered.") : (ushort)id;
    }

    private static void SetString(nint dataObject, ushort format, string value)
    {
        var bytes = new byte[(value.Length + 1) * 2];
        System.Text.Encoding.Unicode.GetBytes(value, bytes);
        SetBytes(dataObject, format, bytes);
    }

    private static unsafe void SetBytes(nint dataObject, ushort format, byte[] bytes)
    {
        var memory = GlobalAlloc(MoveableMemory, (nuint)bytes.Length);
        if (memory == 0) throw new InsufficientMemoryException("The drag data could not be allocated.");
        var locked = GlobalLock(memory);
        if (locked == 0)
        {
            _ = GlobalFree(memory);
            throw new InsufficientMemoryException("The drag data could not be allocated.");
        }
        Marshal.Copy(bytes, 0, locked, bytes.Length);
        _ = GlobalUnlock(memory);
        var formatEtc = new FormatEtc { Format = format, Aspect = ContentAspect, Index = -1, Tymed = HGlobal };
        var medium = new StgMedium { Tymed = HGlobal, Handle = memory };
        // IDataObject::SetData, releasing the memory to the data object.
        var hr = ((delegate* unmanaged[Stdcall]<nint, FormatEtc*, StgMedium*, int, int>)(*(nint**)dataObject)[7])(dataObject, &formatEtc, &medium, 1);
        if (hr < 0)
        {
            _ = GlobalFree(memory);
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    private static unsafe uint? ReadEffect(nint dataObject, string formatName)
    {
        var format = new FormatEtc { Format = Register(formatName), Aspect = ContentAspect, Index = -1, Tymed = HGlobal };
        var medium = default(StgMedium);
        // IDataObject::GetData
        var hr = ((delegate* unmanaged[Stdcall]<nint, FormatEtc*, StgMedium*, int>)(*(nint**)dataObject)[3])(dataObject, &format, &medium);
        if (hr < 0 || medium.Tymed != HGlobal || medium.Handle == 0) return null;
        try
        {
            if (GlobalSize(medium.Handle) < sizeof(uint)) return null;
            var locked = GlobalLock(medium.Handle);
            if (locked == 0) return null;
            try { return *(uint*)locked; }
            finally { _ = GlobalUnlock(medium.Handle); }
        }
        finally
        {
            ReleaseStgMedium(&medium);
        }
    }

    private static unsafe void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)instance)[2])(instance);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FormatEtc
    {
        internal ushort Format;
        internal nint TargetDevice;
        internal uint Aspect;
        internal int Index;
        internal uint Tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StgMedium
    {
        internal uint Tymed;
        internal nint Handle;
        internal nint Release;
    }

    [LibraryImport("ole32.dll")] private static partial int OleInitialize(nint reserved);
    [LibraryImport("ole32.dll")] private static partial void OleUninitialize();
    [LibraryImport("ole32.dll")] private static unsafe partial void ReleaseStgMedium(StgMedium* medium);
    [LibraryImport("shell32.dll")] private static partial int SHDoDragDrop(nint window, nint dataObject, nint dropSource, uint okEffects, out uint effect);
    [LibraryImport("shell32.dll")]
    private static unsafe partial int SHCreateDataObject(nint folder, uint count, nint* items, nint inner, Guid* iid, nint* dataObject);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributes, out uint result);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormat(string format);
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int key);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalFree(nint memory);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll")] private static partial nuint GlobalSize(nint memory);
}
