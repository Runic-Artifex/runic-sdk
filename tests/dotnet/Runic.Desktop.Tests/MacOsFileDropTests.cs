using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// The macOS drop reads the dragging pasteboard of performDragOperation:. This writes file and web URLs to a private
// pasteboard and hands it over as a dragging info, so the real AppKit pasteboard path runs on the macOS runner.
public sealed unsafe partial class MacOsFileDropTests
{
    private static nint s_pasteboard;

    [Test]
    public void TheDraggingPasteboardYieldsFilePathsOnly()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.True(MacOsWkWebViewHost.HasFileDropWebView);
        var root = Directory.CreateTempSubdirectory("runic-mac-drop-");
        try
        {
            var file = Path.Combine(root.FullName, "dropped notes.txt");
            File.WriteAllText(file, "dropped");
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "a folder")).FullName;
            var pasteboard = Send(Class("NSPasteboard"), "pasteboardWithUniqueName");
            Send(pasteboard, "clearContents");
            var urls = new[] { FileUrl(file), FileUrl(folder), WebUrl("https://example.com/remote.txt") };
            fixed (nint* items = urls)
            {
                var array = ((delegate* unmanaged[Cdecl]<nint, nint, nint*, nuint, nint>)MessageSend)(
                    Class("NSArray"), Selector("arrayWithObjects:count:"), items, (nuint)urls.Length);
                Assert.True(((delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)MessageSend)(pasteboard, Selector("writeObjects:"), array) != 0);
            }
            s_pasteboard = pasteboard;
            var info = Send(Send(DraggingInfoClass(), "alloc"), "init");
            try
            {
                var paths = MacOsWkWebViewHost.ReadDroppedFilePaths(info);
                // macOS resolves /var to /private/var for temporary files.
                Assert.Equal([Path.GetFileName(file), Path.GetFileName(folder)], paths.Select(Path.GetFileName));
                Assert.All(paths, static path => Assert.True(Path.IsPathFullyQualified(path)));
            }
            finally
            {
                Send(info, "release");
                ((delegate* unmanaged[Cdecl]<nint, nint, void>)MessageSend)(pasteboard, Selector("releaseGlobally"));
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static nint DraggingInfoClass()
    {
        var existing = Class("RunicTestDraggingInfo");
        if (existing != 0) return existing;
        using var name = new Utf8("RunicTestDraggingInfo");
        var type = AllocateClassPair(Class("NSObject"), name.Pointer, 0);
        using var types = new Utf8("@@:");
        AddMethod(type, Selector("draggingPasteboard"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&DraggingPasteboard, types.Pointer);
        RegisterClassPair(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint DraggingPasteboard(nint self, nint selector) => s_pasteboard;

    private static nint FileUrl(string path) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)MessageSend)(Class("NSURL"), Selector("fileURLWithPath:"), String(path));

    private static nint WebUrl(string url) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)MessageSend)(Class("NSURL"), Selector("URLWithString:"), String(url));

    private static nint String(string value)
    {
        using var text = new Utf8(value);
        return ((delegate* unmanaged[Cdecl]<nint, nint, byte*, nint>)MessageSend)(Class("NSString"), Selector("stringWithUTF8String:"), text.Pointer);
    }

    private static nint Send(nint receiver, string selector) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint>)MessageSend)(receiver, Selector(selector));

    private static nint Class(string name)
    {
        using var text = new Utf8(name);
        return GetClass(text.Pointer);
    }

    private static nint Selector(string name)
    {
        using var text = new Utf8(name);
        return RegisterName(text.Pointer);
    }

    private static nint MessageSend => NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");

    private sealed class Utf8(string value) : IDisposable
    {
        private readonly nint _memory = Marshal.StringToCoTaskMemUTF8(value);
        internal byte* Pointer => (byte*)_memory;
        public void Dispose() => Marshal.FreeCoTaskMem(_memory);
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, EntryPoint = "objc_getClass")] private static partial nint GetClass(byte* name);
    [LibraryImport(ObjC, EntryPoint = "sel_registerName")] private static partial nint RegisterName(byte* name);
    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair")] private static partial nint AllocateClassPair(nint superclass, byte* name, nuint extra);
    [LibraryImport(ObjC, EntryPoint = "class_addMethod")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddMethod(nint type, nint selector, nint implementation, byte* types);
    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")] private static partial void RegisterClassPair(nint type);
}
