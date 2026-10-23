using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// NSDragOperation values and the drag and drop rules of the macOS window. Pure, so every OS tests them.
internal static class MacOsDragOperations
{
    internal const ulong None = 0;
    internal const ulong Copy = 1;
    internal const ulong Link = 2;
    internal const ulong Generic = 4;
    internal const ulong Move = 16;

    // The operations a drag out offers. Move comes with Generic, the operation AppKit targets such as Finder use for
    // a plain move, as WebKit pairs them for dropEffect "move".
    internal static ulong ToMask(DesktopDragEffects effects) =>
        (effects.HasFlag(DesktopDragEffects.Copy) ? Copy : None) |
        (effects.HasFlag(DesktopDragEffects.Link) ? Link : None) |
        (effects.HasFlag(DesktopDragEffects.Move) ? Move | Generic : None);

    // An NSDragOperation (mask) as effects; Generic counts as Move. Private and Delete have no effect.
    internal static DesktopDragEffects FromOperation(ulong operation) =>
        ((operation & Copy) != 0 ? DesktopDragEffects.Copy : DesktopDragEffects.None) |
        ((operation & Link) != 0 ? DesktopDragEffects.Link : DesktopDragEffects.None) |
        ((operation & (Move | Generic)) != 0 ? DesktopDragEffects.Move : DesktopDragEffects.None);

    // The effect of a drop: what the drag source allows (with the user's modifier keys) and WebKit last accepted for
    // the page, Move before Copy before Link; Copy when they share none.
    internal static DesktopDragEffects DropEffect(ulong sourceMask, ulong accepted)
    {
        var effect = DesktopDragRules.Single(FromOperation(sourceMask & accepted),
            DesktopDragEffects.Copy | DesktopDragEffects.Move | DesktopDragEffects.Link);
        return effect == DesktopDragEffects.None ? DesktopDragEffects.Copy : effect;
    }

    // Whether a frame's drop report counts (D037.2): the main frame's always, a subframe's only when its security
    // origin is the presented origin. frameOrigin is MacOsWebContent.OriginUrl of the frame's WKSecurityOrigin.
    internal static bool AcceptsFrame(bool mainFrame, Uri? presented, string? frameOrigin) =>
        mainFrame || (presented is not null && PresentationSecurityPolicy.IsSameOrigin(presented, frameOrigin));
}

// Drags out of the window (beginDraggingSessionWithItems:event:source:), the drop effect WebKit negotiates, and file
// promises dropped onto the window (NSFilePromiseReceiver) received as read leases.
internal sealed partial class MacOsWkWebViewHost
{
    // How long a drop waits for its promised files before it is delivered with those that arrived.
    internal static readonly TimeSpan PromiseWait = TimeSpan.FromSeconds(30);
    private const int BlockHasSignature = 1 << 30;

    private static readonly Lazy<nint> DragSourceClass = new(CreateDragSourceClass);
    // Drags out by their NSDraggingSource object, until AppKit reports the end.
    private static readonly ConcurrentDictionary<nint, DragSession> DragSessions = new();
    // Promised file receptions by the context of their reader block.
    private static readonly ConcurrentDictionary<long, PromiseTarget> PromiseTargets = new();
    // The NSOperationQueue the reader blocks run on, for the process lifetime.
    private static readonly Lazy<nint> PromiseQueue = new(CreatePromiseQueue);
    private static readonly Lazy<nint> ReaderDescriptor = new(CreateReaderDescriptor);
    private static long s_promiseKey;

    // The NSDragOperation WebKit last answered for a drag over the web view. Main thread only.
    private ulong _dragOperation;
    // A drop with promised files still arriving, which takes the page's next report; under _promiseGate, which also
    // orders the reports passed to _drops.
    private readonly object _promiseGate = new();
    private PromisedDrop? _awaitingReport;

    private sealed class DragSession(MacOsWkWebViewHost? owner, DesktopDragEffects allowed)
    {
        internal MacOsWkWebViewHost? Owner { get; } = owner;
        internal DesktopDragEffects Allowed { get; } = allowed;
        internal TaskCompletionSource<DesktopDragEffects> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // A drag on the main thread while the primary button is held, as Electron's startDrag: AppKit tracks it after this
    // returns, and the task completes when the session ends.
    public ValueTask<DesktopDragEffects> StartDragAsync(DesktopDragData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();
        if (DragSourceClass.Value == 0) throw new NotSupportedException("The AppKit dragging source class could not be created.");
        var session = new DragSession(this, data.AllowedEffects);
        var started = InvokeAsync(() => BeginDrag(session, data), cancellationToken);
        return new ValueTask<DesktopDragEffects>(WaitForDragAsync(session, started));
    }

    private static async Task<DesktopDragEffects> WaitForDragAsync(DragSession session, ValueTask started)
    {
        await started.ConfigureAwait(false);
        return await session.Result.Task.ConfigureAwait(false);
    }

    // Main thread only. The event that starts the session is synthesized at the pointer: the page's dragstart came
    // from the web process, so no AppKit mouse event of this process belongs to the drag.
    private void BeginDrag(DragSession session, DesktopDragData data)
    {
        // Without a held button AppKit would start a drag that follows no pointer and never ends.
        if (!IsOpen || (Api.SendUlong(Api.GetClass("NSEvent"), "pressedMouseButtons") & 1) == 0)
        {
            session.Result.TrySetResult(DesktopDragEffects.None);
            return;
        }
        var pool = Api.SendNint(Api.SendNint(Api.GetClass("NSAutoreleasePool"), "alloc"), "init");
        nint source = 0;
        try
        {
            var location = Api.SendPoint(_window, "mouseLocationOutsideOfEventStream");
            var items = Api.NewDraggingItems(Api.ConvertPointFromWindow(_webView, location), data);
            var dragEvent = Api.MouseDraggedEvent(location, Api.SendNint(_window, "windowNumber"));
            source = Api.SendNint(Api.SendNint(DragSourceClass.Value, "alloc"), "init");
            DragSessions[source] = session;
            if (dragEvent != 0 && Api.BeginDraggingSession(_webView, items, dragEvent, source) != 0)
            {
                // Released when the session ends (DragSessionEnded).
                source = 0;
            }
            else
            {
                session.Result.TrySetResult(DesktopDragEffects.None);
            }
        }
        finally
        {
            if (source != 0)
            {
                DragSessions.TryRemove(source, out _);
                Api.SendVoid(source, "release");
            }
            Api.SendVoid(pool, "drain");
        }
    }

    // A closing window ends its drags with None; AppKit still ends the session, which releases the source.
    private void EndDrags()
    {
        foreach (var drag in DragSessions.Values)
        {
            if (ReferenceEquals(drag.Owner, this)) drag.Result.TrySetResult(DesktopDragEffects.None);
        }
    }

    // For Runic.Desktop.Tests: a dragging source with a pending drag of no window, ended through its methods.
    internal static nint NewPendingDragSource(DesktopDragEffects allowed, out Task<DesktopDragEffects> result)
    {
        var session = new DragSession(null, allowed);
        var source = Api.SendNint(Api.SendNint(DragSourceClass.Value, "alloc"), "init");
        DragSessions[source] = session;
        result = session.Result.Task;
        return source;
    }

    // An NSObject that is the NSDraggingSource of the window's drags out.
    private static unsafe nint CreateDragSourceClass()
    {
        if (!OperatingSystem.IsMacOS() || !Api.IsAvailable)
        {
            return 0;
        }
        var existing = Api.GetClass("RunicDesktopDragSource");
        if (existing != 0)
        {
            return existing;
        }
        var type = Api.AllocateClassPair(Api.GetClass("NSObject"), "RunicDesktopDragSource");
        if (type == 0)
        {
            return 0;
        }
        Api.AddProtocol(type, "NSDraggingSource");
        // NSDragOperation is NSUInteger and NSDraggingContext NSInteger; the end point is an NSPoint by value.
        Api.AddMethod(type, "draggingSession:sourceOperationMaskForDraggingContext:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, ulong>)&DragSourceOperationMask, "Q@:@q");
        Api.AddMethod(type, "draggingSession:endedAtPoint:operation:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, CGPoint, ulong, void>)&DragSessionEnded, "v@:@{CGPoint=dd}Q");
        Api.RegisterClassPair(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong DragSourceOperationMask(nint self, nint selector, nint session, nint context)
    {
        try
        {
            return DragSessions.TryGetValue(self, out var drag) ? MacOsDragOperations.ToMask(drag.Allowed) : MacOsDragOperations.None;
        }
        catch
        {
            return MacOsDragOperations.None;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DragSessionEnded(nint self, nint selector, nint session, CGPoint point, ulong operation)
    {
        try
        {
            if (!DragSessions.TryRemove(self, out var drag)) return;
            drag.Result.TrySetResult(DesktopDragRules.Single(MacOsDragOperations.FromOperation(operation), drag.Allowed));
            // AppKit may still message its source after this returns; the event's pool releases it.
            Api.SendNint(self, "autorelease");
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    // draggingEntered: and draggingUpdated: of the web view: WebKit's answer, which the page's dropEffect and
    // data-runic-drop-effect decide, is the operation the drop performs.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong DraggingUpdated(nint self, nint selector, nint sender)
    {
        try
        {
            var operation = Api.SendSuperUlongNint(self, Api.GetClass("WKWebView"), selector, sender);
            if (WebViews.TryGetValue(self, out var host)) host._dragOperation = operation;
            return operation;
        }
        catch
        {
            return MacOsDragOperations.None;
        }
    }

    // performDragOperation:, main thread. Paths and the effect are read now; promised files are received into private
    // staging directories, and the drop goes to the pairing once they arrived.
    private void ReadDrop(nint draggingInfo)
    {
        var paths = Api.ReadDroppedFilePaths(draggingInfo);
        var effect = MacOsDragOperations.DropEffect(Api.SendUlong(draggingInfo, "draggingSourceOperationMask"), _dragOperation);
        var receivers = Api.ReadPromiseReceivers(draggingInfo);
        if (receivers.Count == 0)
        {
            lock (_promiseGate)
            {
                // A new drop: an earlier promised drop's report is not coming.
                _awaitingReport = null;
                _drops?.NativeDrop(paths, [], effect);
            }
            return;
        }
        var drop = new PromisedDrop(paths, effect);
        lock (_promiseGate) _awaitingReport = drop;
        _ = DeliverPromisedDropAsync(drop, ReceivePromisedFiles(receivers, PromiseWait));
    }

    // The page's report of a drop, main thread. While a promised drop receives its files, the report waits with it:
    // the pairing waits only FileDropSupport.LabelWait for a report after the native drop.
    private void PageReport(string? label, bool refused)
    {
        lock (_promiseGate)
        {
            if (_awaitingReport is { } drop)
            {
                drop.Report(label, refused);
                _awaitingReport = null;
                return;
            }
            _drops?.PageReport(label, refused);
        }
    }

    private async Task DeliverPromisedDropAsync(PromisedDrop drop, Task<IReadOnlyList<IReadFileLease>> received)
    {
        IReadOnlyList<IReadFileLease> files;
        try
        {
            files = await received.ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            files = [];
        }
        lock (_promiseGate)
        {
            if (ReferenceEquals(_awaitingReport, drop)) _awaitingReport = null;
            if (drop.Paths.Count == 0 && files.Count == 0) return;
            if (_drops is not { } drops)
            {
                foreach (var file in files) _ = file.DisposeAsync().AsTask().ContinueWith(static task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                return;
            }
            // A closed window's pairing discards the drop and deletes the staged copies.
            drops.NativeDrop(drop.Paths, files, drop.Effect);
            if (drop.Reported) drops.PageReport(drop.Label, drop.Refused);
        }
    }

    // A drop whose promised files are still arriving, and the page's report for it. Under _promiseGate.
    private sealed class PromisedDrop(IReadOnlyList<string> paths, DesktopDragEffects effect)
    {
        internal IReadOnlyList<string> Paths { get; } = paths;
        internal DesktopDragEffects Effect { get; } = effect;
        internal bool Reported { get; private set; }
        internal string? Label { get; private set; }
        internal bool Refused { get; private set; }

        internal void Report(string? label, bool refused)
        {
            Reported = true;
            Label = label;
            Refused = refused;
        }
    }

    // The NSFilePromiseReceiver objects on a drag's pasteboard; for Runic.Desktop.Tests too.
    internal static List<nint> ReadPromiseReceivers(nint draggingInfo) => Api.ReadPromiseReceivers(draggingInfo);

    // Main thread. Receives every receiver's files, each receiver into its own private staging directory, and
    // completes with a read lease per received file once all arrived or the wait ended. A receiver reports one file
    // per promised file type.
    internal static Task<IReadOnlyList<IReadFileLease>> ReceivePromisedFiles(IReadOnlyList<nint> receivers, TimeSpan wait)
    {
        var expected = 0;
        foreach (var receiver in receivers)
        {
            expected += (int)Math.Clamp(Api.SendUlong(Api.SendNint(receiver, "fileTypes"), "count"), 1, 1024);
        }
        var reception = new PromiseReception(expected);
        List<(long Key, string Directory)> targets = [];
        List<nint> retained = [];
        try
        {
            foreach (var receiver in receivers)
            {
                // Mode 0700 on macOS: only this user reads what the source writes there.
                var directory = Directory.CreateTempSubdirectory("runic-drop-").FullName;
                var key = Interlocked.Increment(ref s_promiseKey);
                PromiseTargets[key] = new PromiseTarget(reception, directory);
                targets.Add((key, directory));
                // Kept until the reception ends: the reader runs after this returns.
                Api.SendNint(receiver, "retain");
                retained.Add(receiver);
                Api.ReceivePromisedFiles(receiver, directory, PromiseQueue.Value, key);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The receivers that started still deliver until the wait ends; the others never will.
            reception.Abandon();
        }
        return FinishReceptionAsync(reception, targets, retained, wait);
    }

    private static async Task<IReadOnlyList<IReadFileLease>> FinishReceptionAsync(PromiseReception reception,
        List<(long Key, string Directory)> targets, List<nint> receivers, TimeSpan wait)
    {
        try
        {
            await reception.Done.Task.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Delivered with the files that arrived.
        }
        var files = reception.Close();
        foreach (var (key, directory) in targets)
        {
            PromiseTargets.TryRemove(key, out _);
            // Received files moved to their own directories; what is left failed, is a directory or came late.
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        foreach (var receiver in receivers) Api.SendVoid(receiver, "release");
        return files;
    }

    private sealed record PromiseTarget(PromiseReception Reception, string Directory);

    // The files of one drop's receivers. A reader block reports each file once, on the promise queue.
    private sealed class PromiseReception(int expected)
    {
        private readonly object _gate = new();
        private readonly List<IReadFileLease> _files = [];
        private int _remaining = expected;
        private bool _closed;

        internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // path: the received file, null when the reception failed. Only a regular file of the receiver's staging
        // directory, by name, is taken.
        internal void Received(string directory, string? path)
        {
            lock (_gate)
            {
                if (_closed) return;
                var name = path is null ? "" : Path.GetFileName(path);
                var staged = Path.Combine(directory, name);
                if (name.Length > 0 && File.Exists(staged))
                {
                    try
                    {
                        _files.Add(Stage(staged));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        // Deleted with the staging directory.
                    }
                }
                if (--_remaining <= 0) Done.TrySetResult();
            }
        }

        internal void Abandon() => Done.TrySetResult();

        internal IReadOnlyList<IReadFileLease> Close()
        {
            lock (_gate)
            {
                _closed = true;
                return [.. _files];
            }
        }

        // Moves a received file into a directory of its own, which its lease deletes: a receiver may promise several.
        private static StagedFileLease Stage(string staged)
        {
            var own = Directory.CreateTempSubdirectory("runic-drop-").FullName;
            try
            {
                var target = Path.Combine(own, Path.GetFileName(staged));
                File.Move(staged, target);
                return new StagedFileLease(target, own);
            }
            catch
            {
                Directory.Delete(own, recursive: true);
                throw;
            }
        }
    }

    // The reader block of receivePromisedFilesAtDestination:options:operationQueue:reader:, void (^)(NSURL *, NSError *).
    // Its context field is the reception's key, not a GCHandle: a late call after the wait finds no target.
    [StructLayout(LayoutKind.Sequential)]
    private struct ReaderBlock
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public nint Descriptor;
        public long Context;
    }

    // Without copy and dispose helpers: Block_copy copies the context with the rest of the block.
    [StructLayout(LayoutKind.Sequential)]
    private struct ReaderBlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
        public nint Signature;
    }

    private static nint CreatePromiseQueue() => Api.SendNint(Api.SendNint(Api.GetClass("NSOperationQueue"), "alloc"), "init");

    // Process lifetime, as the block's type encoding.
    private static unsafe nint CreateReaderDescriptor()
    {
        var descriptor = (ReaderBlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(ReaderBlockDescriptor));
        descriptor->Size = (nuint)sizeof(ReaderBlock);
        descriptor->Signature = Marshal.StringToCoTaskMemUTF8("v@?@@");
        return (nint)descriptor;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void PromisedFileReceived(ReaderBlock* block, nint url, nint error)
    {
        try
        {
            if (!PromiseTargets.TryGetValue(block->Context, out var target)) return;
            var path = error == 0 && url != 0 && Api.SendBool(url, "isFileURL") ? Api.Text(Api.SendNint(url, "path")) : null;
            target.Reception.Received(target.Directory, path);
        }
        catch
        {
            // Never unwind through Foundation.
        }
    }

#pragma warning disable CA1822
    private sealed unsafe partial class ObjC
    {
        private nint _stackBlock;

        // The class of a block literal on the stack, which Block_copy moves to the heap.
        private nint StackBlock => _stackBlock != 0
            ? _stackBlock
            : _stackBlock = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteStackBlock");

        internal void AddProtocol(nint type, string name)
        {
            using var value = Utf8String.Create(name);
            var protocol = Native.ObjCGetProtocol(value.Pointer);
            if (protocol != 0) Native.ClassAddProtocol(type, protocol);
        }

        internal bool IsString(nint value) => value != 0 && SendBool(value, "isKindOfClass:", GetClass("NSString"));

        // [view convertPoint:point fromView:nil]: window base coordinates to the view's. NSPoint comes back in registers.
        internal CGPoint ConvertPointFromWindow(nint view, CGPoint point) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGPoint, nint, CGPoint>)_messageSend)(view, Selector("convertPoint:fromView:"), point, 0);

        // A left mouse dragged event at a point of the window (base coordinates), stamped now.
        internal nint MouseDraggedEvent(CGPoint location, nint windowNumber)
        {
            const ulong LeftMouseDragged = 6; // NSEventTypeLeftMouseDragged
            var now = SendDouble(SendNint(GetClass("NSProcessInfo"), "processInfo"), "systemUptime");
            return ((delegate* unmanaged[Cdecl]<nint, nint, ulong, CGPoint, ulong, double, nint, nint, nint, nint, float, nint>)_messageSend)(
                GetClass("NSEvent"),
                Selector("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
                LeftMouseDragged, location, 0, now, windowNumber, 0, 0, 1, 1f);
        }

        // An autoreleased array of NSDraggingItem: a file URL per path, the text as plain text, and each URI as a URL
        // with its string. Each item shows a small icon at the pointer (view coordinates), a stack for several.
        internal nint NewDraggingItems(CGPoint at, DesktopDragData data)
        {
            var items = SendNint(GetClass("NSMutableArray"), "array");
            var workspace = SendNint(GetClass("NSWorkspace"), "sharedWorkspace");
            var index = 0;
            void Add(nint writer, nint icon)
            {
                var item = SendNintNint(SendNint(GetClass("NSDraggingItem"), "alloc"), "initWithPasteboardWriter:", writer);
                var offset = Math.Min(index++, 8) * 4;
                SetDraggingFrame(item, new CGRect(at.X - 16 + offset, at.Y - 16 + offset, 32, 32), icon);
                SendVoidNint(items, "addObject:", item);
                SendVoid(item, "release");
            }
            foreach (var path in data.Paths.IsDefault ? [] : data.Paths)
            {
                var text = CreateString(path);
                try
                {
                    // NSURL writes public.file-url and the older file name types Finder and other targets read.
                    Add(SendNintNint(GetClass("NSURL"), "fileURLWithPath:", text), SendNintNint(workspace, "iconForFile:", text));
                }
                finally
                {
                    SendVoid(text, "release");
                }
            }
            if (data.Text is { } plain)
            {
                var item = NewPasteboardItem((plain, "public.utf8-plain-text"));
                Add(item, ContentTypeIcon(workspace, "public.plain-text"));
                SendVoid(item, "release");
            }
            foreach (var uri in data.Uris.IsDefault ? [] : data.Uris)
            {
                var item = NewPasteboardItem((uri.AbsoluteUri, "public.url"), (uri.AbsoluteUri, "public.utf8-plain-text"));
                Add(item, ContentTypeIcon(workspace, "public.url"));
                SendVoid(item, "release");
            }
            return items;
        }

        private nint NewPasteboardItem(params ReadOnlySpan<(string Value, string Type)> entries)
        {
            var item = SendNint(SendNint(GetClass("NSPasteboardItem"), "alloc"), "init");
            foreach (var (value, type) in entries)
            {
                var text = CreateString(value);
                var name = CreateString(type);
                ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, byte>)_messageSend)(item, Selector("setString:forType:"), text, name);
                SendVoid(name, "release");
                SendVoid(text, "release");
            }
            return item;
        }

        // The system icon of a content type (UTType, macOS 11), or nil: the item then drags without an image.
        private nint ContentTypeIcon(nint workspace, string identifier)
        {
            var typeClass = GetClass("UTType");
            if (typeClass == 0 || !RespondsToSelector(workspace, "iconForContentType:")) return 0;
            var text = CreateString(identifier);
            try
            {
                var type = SendNintNint(typeClass, "typeWithIdentifier:", text);
                return type == 0 ? 0 : SendNintNint(workspace, "iconForContentType:", type);
            }
            finally
            {
                SendVoid(text, "release");
            }
        }

        private void SetDraggingFrame(nint item, CGRect frame, nint contents) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGRect, nint, void>)_messageSend)(item, Selector("setDraggingFrame:contents:"), frame, contents);

        // The NSDraggingSession, or nil when AppKit started none.
        internal nint BeginDraggingSession(nint view, nint items, nint dragEvent, nint source) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint>)_messageSend)(
                view, Selector("beginDraggingSessionWithItems:event:source:"), items, dragEvent, source);

        internal ulong SendSuperUlongNint(nint receiver, nint superclass, nint selector, nint value)
        {
            var super = new ObjCSuper(receiver, superclass);
            return ((delegate* unmanaged[Cdecl]<ObjCSuper*, nint, nint, ulong>)_messageSendSuper)(&super, selector, value);
        }

        internal List<nint> ReadPromiseReceivers(nint draggingInfo)
        {
            List<nint> receivers = [];
            var type = GetClass("NSFilePromiseReceiver");
            var pasteboard = SendNint(draggingInfo, "draggingPasteboard");
            if (type == 0 || pasteboard == 0)
            {
                return receivers;
            }
            var objects = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)_messageSend)(
                pasteboard, Selector("readObjectsForClasses:options:"), SendNintNint(GetClass("NSArray"), "arrayWithObject:", type), 0);
            var count = objects == 0 ? 0 : (long)SendUlong(objects, "count");
            for (long index = 0; index < count; index++)
            {
                if (SendNintNint(objects, "objectAtIndex:", (nint)index) is var receiver and not 0) receivers.Add(receiver);
            }
            return receivers;
        }

        // [receiver receivePromisedFilesAtDestination:options:operationQueue:reader:] with a reader block whose
        // context is key. Foundation copies the block for the asynchronous reception.
        internal void ReceivePromisedFiles(nint receiver, string directory, nint queue, long key)
        {
            var path = CreateString(directory);
            try
            {
                var destination = ((delegate* unmanaged[Cdecl]<nint, nint, nint, byte, nint>)_messageSend)(
                    GetClass("NSURL"), Selector("fileURLWithPath:isDirectory:"), path, 1);
                var options = SendNint(GetClass("NSDictionary"), "dictionary");
                var block = new ReaderBlock
                {
                    Isa = StackBlock,
                    Flags = BlockHasSignature,
                    Invoke = (nint)(delegate* unmanaged[Cdecl]<ReaderBlock*, nint, nint, void>)&PromisedFileReceived,
                    Descriptor = ReaderDescriptor.Value,
                    Context = key,
                };
                var copy = Native.BlockCopy((nint)(&block));
                if (copy == 0) throw new IOException("Foundation could not copy the promised file reader block.");
                try
                {
                    ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)_messageSend)(
                        receiver, Selector("receivePromisedFilesAtDestination:options:operationQueue:reader:"), destination, options, queue, copy);
                }
                finally
                {
                    Native.BlockRelease(copy);
                }
            }
            finally
            {
                SendVoid(path, "release");
            }
        }
    }
#pragma warning restore CA1822

    private static partial class Native
    {
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getProtocol")]
        internal static unsafe partial nint ObjCGetProtocol(byte* name);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "class_addProtocol")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ClassAddProtocol(nint type, nint protocol);

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_copy")]
        internal static partial nint BlockCopy(nint block);

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_release")]
        internal static partial void BlockRelease(nint block);
    }
}
