using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// The NSDragOperation rules of the macOS window, on every OS.
public sealed class MacOsDragOperationTests
{
    [Test]
    public void DragOutOffersTheAllowedOperations()
    {
        Assert.Equal(MacOsDragOperations.Copy, MacOsDragOperations.ToMask(DesktopDragEffects.Copy));
        Assert.Equal(MacOsDragOperations.Move | MacOsDragOperations.Generic, MacOsDragOperations.ToMask(DesktopDragEffects.Move));
        Assert.Equal(MacOsDragOperations.Link, MacOsDragOperations.ToMask(DesktopDragEffects.Link));
        Assert.Equal(23UL, MacOsDragOperations.ToMask(DesktopDragEffects.Copy | DesktopDragEffects.Move | DesktopDragEffects.Link));
        Assert.Equal(MacOsDragOperations.None, MacOsDragOperations.ToMask(DesktopDragEffects.None));
    }

    [Test]
    public void OperationsMapToEffects()
    {
        Assert.Equal(DesktopDragEffects.None, MacOsDragOperations.FromOperation(0));
        Assert.Equal(DesktopDragEffects.Move, MacOsDragOperations.FromOperation(MacOsDragOperations.Generic));
        Assert.Equal(DesktopDragEffects.Move, MacOsDragOperations.FromOperation(MacOsDragOperations.Move));
        Assert.Equal(DesktopDragEffects.Copy | DesktopDragEffects.Link, MacOsDragOperations.FromOperation(3));
        // NSDragOperationPrivate and Delete are no effect of a Runic drag.
        Assert.Equal(DesktopDragEffects.None, MacOsDragOperations.FromOperation(8 | 32));
    }

    [Test]
    public void TheDropEffectIsWhatTheSourceAllowsAndWebKitAccepted()
    {
        const ulong finder = 1 | 2 | 4 | 16;
        Assert.Equal(DesktopDragEffects.Move, MacOsDragOperations.DropEffect(finder, MacOsDragOperations.Move));
        Assert.Equal(DesktopDragEffects.Copy, MacOsDragOperations.DropEffect(finder, MacOsDragOperations.Copy));
        Assert.Equal(DesktopDragEffects.Link, MacOsDragOperations.DropEffect(MacOsDragOperations.Link, MacOsDragOperations.Link | MacOsDragOperations.Copy));
        // Move before Copy before Link when WebKit answered several.
        Assert.Equal(DesktopDragEffects.Move, MacOsDragOperations.DropEffect(finder, finder));
        // Nothing in common, or nothing answered yet: Copy, the default of a drop.
        Assert.Equal(DesktopDragEffects.Copy, MacOsDragOperations.DropEffect(MacOsDragOperations.Copy, MacOsDragOperations.Move));
        Assert.Equal(DesktopDragEffects.Copy, MacOsDragOperations.DropEffect(finder, MacOsDragOperations.None));
    }

    [Test]
    public void OnlyTheMainFrameAndFramesOfThePresentedOriginReportDrops()
    {
        var presented = new Uri("http://127.0.0.1:5173/app/index.html");
        Assert.True(MacOsDragOperations.AcceptsFrame(mainFrame: true, presented, null));
        Assert.True(MacOsDragOperations.AcceptsFrame(false, presented, MacOsWebContent.OriginUrl("http", "127.0.0.1", 5173)));
        Assert.False(MacOsDragOperations.AcceptsFrame(false, presented, MacOsWebContent.OriginUrl("http", "127.0.0.1", 5174)));
        Assert.False(MacOsDragOperations.AcceptsFrame(false, presented, MacOsWebContent.OriginUrl("https", "127.0.0.1", 5173)));
        Assert.False(MacOsDragOperations.AcceptsFrame(false, presented, MacOsWebContent.OriginUrl("http", "example.com", 0)));
        Assert.False(MacOsDragOperations.AcceptsFrame(false, presented, null));
        Assert.False(MacOsDragOperations.AcceptsFrame(false, null, "http://127.0.0.1:5173"));
        // WKSecurityOrigin reports port 0 for the scheme's default.
        Assert.True(MacOsDragOperations.AcceptsFrame(false, new Uri("https://app.example/"), MacOsWebContent.OriginUrl("https", "app.example", 0)));
    }
}

// The macOS drop reads the dragging pasteboard of performDragOperation:. This writes file and web URLs to a private
// pasteboard and hands it over as a dragging info, so the real AppKit pasteboard path runs on the macOS runner.
public sealed unsafe partial class MacOsFileDropTests
{
    private static nint s_pasteboard;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> s_destinations = new();

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

    // An NSFilePromiseProvider on the pasteboard, as Mail or Photos put one, reaches the drop as a promise receiver
    // and not as a path.
    [Test]
    public void TheDraggingPasteboardYieldsFilePromiseReceivers()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var pool = Send(Send(Class("NSAutoreleasePool"), "alloc"), "init");
        try
        {
            var provider = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)MessageSend)(
                Send(Class("NSFilePromiseProvider"), "alloc"), Selector("initWithFileType:delegate:"), String("public.plain-text"),
                Send(Send(PromiseProviderDelegateClass(), "alloc"), "init"));
            var pasteboard = Send(Class("NSPasteboard"), "pasteboardWithUniqueName");
            Send(pasteboard, "clearContents");
            var array = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)MessageSend)(Class("NSArray"), Selector("arrayWithObject:"), provider);
            Assert.True(((delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)MessageSend)(pasteboard, Selector("writeObjects:"), array) != 0);
            s_pasteboard = pasteboard;
            var info = Send(Send(DraggingInfoClass(), "alloc"), "init");
            try
            {
                Assert.Empty(MacOsWkWebViewHost.ReadDroppedFilePaths(info));
                var receiver = Assert.Single(MacOsWkWebViewHost.ReadPromiseReceivers(info));
                var types = Send(receiver, "fileTypes");
                Assert.Equal(1L, (long)Send(types, "count"));
                Assert.Equal("public.plain-text", Text(((delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)MessageSend)(types, Selector("objectAtIndex:"), 0)));
            }
            finally
            {
                Send(info, "release");
                Send(provider, "release");
                ((delegate* unmanaged[Cdecl]<nint, nint, void>)MessageSend)(pasteboard, Selector("releaseGlobally"));
            }
        }
        finally
        {
            Send(pool, "drain");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Point(double X, double Y);

    private static nint PromiseProviderDelegateClass()
    {
        var existing = Class("RunicTestFilePromiseDelegate");
        if (existing != 0) return existing;
        using var name = new Utf8("RunicTestFilePromiseDelegate");
        var type = AllocateClassPair(Class("NSObject"), name.Pointer, 0);
        using var protocol = new Utf8("NSFilePromiseProviderDelegate");
        if (GetProtocol(protocol.Pointer) is var conforms and not 0) AddProtocol(type, conforms);
        using var fileName = new Utf8("@@:@@");
        AddMethod(type, Selector("filePromiseProvider:fileNameForType:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)&PromisedFileName, fileName.Pointer);
        using var write = new Utf8("v@:@@@?");
        AddMethod(type, Selector("filePromiseProvider:writePromiseToURL:completionHandler:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&WritePromise, write.Pointer);
        RegisterClassPair(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint PromisedFileName(nint self, nint selector, nint provider, nint type) => String("promised.txt");

    // Not reached by the pasteboard check; writes the file and calls completionHandler(nil) should a reception ask.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WritePromise(nint self, nint selector, nint provider, nint url, nint completion)
    {
        try { File.WriteAllText(Text(Send(url, "path")), "promised"); }
        catch (IOException) { }
        ((delegate* unmanaged[Cdecl]<nint, nint, void>)BlockInvoke(completion))(completion, 0);
    }

    // An NSFilePromiseReceiver stand-in: one promised plain-text file, received at once on the calling thread.
    private static nint PromiseReceiverClass(string className, bool fails)
    {
        var existing = Class(className);
        if (existing != 0) return existing;
        using var name = new Utf8(className);
        var type = AllocateClassPair(Class("NSObject"), name.Pointer, 0);
        using var fileTypes = new Utf8("@@:");
        AddMethod(type, Selector("fileTypes"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&FileTypes, fileTypes.Pointer);
        using var receive = new Utf8("v@:@@@@?");
        AddMethod(type, Selector("receivePromisedFilesAtDestination:options:operationQueue:reader:"), fails
            ? (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&FailReception
            : (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&Receive, receive.Pointer);
        RegisterClassPair(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint FileTypes(nint self, nint selector) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)MessageSend)(Class("NSArray"), Selector("arrayWithObject:"), String("public.plain-text"));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Receive(nint self, nint selector, nint destination, nint options, nint queue, nint reader)
    {
        var directory = Text(Send(destination, "path"));
        s_destinations.Enqueue(directory);
        var path = Path.Combine(directory, "mail attachment.txt");
        File.WriteAllText(path, "promised");
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)BlockInvoke(reader))(reader, FileUrl(path), 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FailReception(nint self, nint selector, nint destination, nint options, nint queue, nint reader)
    {
        s_destinations.Enqueue(Text(Send(destination, "path")));
        var error = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint>)MessageSend)(
            Class("NSError"), Selector("errorWithDomain:code:userInfo:"), String("RunicTest"), 1, 0);
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)BlockInvoke(reader))(reader, 0, error);
    }

    // A block literal's invoke function follows isa, flags and reserved.
    private static nint BlockInvoke(nint block) => *(nint*)(block + 16);

    private static string Text(nint value) => Marshal.PtrToStringUTF8(Send(value, "UTF8String")) ?? "";

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
    [LibraryImport(ObjC, EntryPoint = "objc_getProtocol")] private static partial nint GetProtocol(byte* name);
    [LibraryImport(ObjC, EntryPoint = "class_addProtocol")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddProtocol(nint type, nint protocol);
}

// The awaiting cases, outside the unsafe part: an await cannot be in an unsafe context.
public sealed partial class MacOsFileDropTests
{
    // The reception through the reader block: a receiver that writes its file into the staging directory and one that
    // fails. The drop gets one read lease; every staging directory is gone once the lease is disposed.
    [Test]
    public async Task PromisedFilesArriveAsReadLeases()
    {
        if (!OperatingSystem.IsMacOS()) return;
        while (s_destinations.TryDequeue(out _)) { }
        var received = Send(Send(PromiseReceiverClass("RunicTestPromiseReceiver", fails: false), "alloc"), "init");
        var failing = Send(Send(PromiseReceiverClass("RunicTestFailingPromiseReceiver", fails: true), "alloc"), "init");
        try
        {
            var files = await MacOsWkWebViewHost.ReceivePromisedFiles([received, failing], TimeSpan.FromSeconds(10));
            var file = Assert.Single(files);
            Assert.Equal("mail attachment.txt", file.DisplayName);
            Assert.Equal(2, s_destinations.Count);
            Assert.All(s_destinations, static directory => Assert.False(Directory.Exists(directory)));
            await using (var stream = await file.OpenReadAsync())
            using (var reader = new StreamReader(stream))
            {
                Assert.Equal("promised", await reader.ReadToEndAsync());
            }
            await file.DisposeAsync();
        }
        finally
        {
            Send(received, "release");
            Send(failing, "release");
        }
    }

    // AppKit's NSDraggingSource calls, sent to the window's dragging source: the allowed mask, then the end of the
    // session completes the drag with the effect the target chose among the allowed ones.
    [Test]
    public async Task TheDraggingSourceReportsTheAllowedMaskAndTheEndedOperation()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var source = MacOsWkWebViewHost.NewPendingDragSource(DesktopDragEffects.Copy | DesktopDragEffects.Move, out var moved);
        // Once the session ended, the source offers nothing.
        Assert.Equal((1UL | 4 | 16, 0UL), End(source, 16));
        Assert.Equal(DesktopDragEffects.Move, await moved.WaitScaledAsync(TimeSpan.FromSeconds(10)));

        source = MacOsWkWebViewHost.NewPendingDragSource(DesktopDragEffects.Copy, out var refused);
        Assert.Equal((1UL, 0UL), End(source, 2));
        Assert.Equal(DesktopDragEffects.None, await refused.WaitScaledAsync(TimeSpan.FromSeconds(10)));

        source = MacOsWkWebViewHost.NewPendingDragSource(DesktopDragEffects.Copy | DesktopDragEffects.Link, out var cancelled);
        Assert.Equal((3UL, 0UL), End(source, 0));
        Assert.Equal(DesktopDragEffects.None, await cancelled.WaitScaledAsync(TimeSpan.FromSeconds(10)));

        // The mask before and after draggingSession:endedAtPoint:operation:, within one pool on this thread: the source
        // autoreleases itself when its session ends.
        static unsafe (ulong Before, ulong After) End(nint source, ulong operation)
        {
            var pool = Send(Send(Class("NSAutoreleasePool"), "alloc"), "init");
            try
            {
                var before = OperationMask(source);
                ((delegate* unmanaged[Cdecl]<nint, nint, nint, Point, ulong, void>)MessageSend)(
                    source, Selector("draggingSession:endedAtPoint:operation:"), 0, new Point(120, 80), operation);
                return (before, OperationMask(source));
            }
            finally
            {
                Send(pool, "drain");
            }
        }

        static unsafe ulong OperationMask(nint source) => ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, ulong>)MessageSend)(
            source, Selector("draggingSession:sourceOperationMaskForDraggingContext:"), 0, 0);
    }
}
