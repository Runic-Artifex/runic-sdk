using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Runic.Platform.Runtime;

namespace Runic.Platform.MacOS;

internal sealed partial class MacOsFilePicker(INativeWindowOwner owner) : INativeFilePicker, INativeDirectoryPicker
{
    internal TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly Lazy<bool> Sandboxed = new(IsSandboxed);
    // A sandbox grants the selected URL only, never sibling staging.
    public bool SupportsAtomicReplace => !Sandboxed.Value;

    public bool SupportsMultipleSelection => true;

    public bool SupportsMultipleDirectorySelection => true;

    public ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SelectCoreAsync(request, false, cancellationToken);
    }

    public async ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken)
    {
        var selections = await SelectCoreAsync(new NativeFileDialogRequest(false, null) { InitialDirectory = initialDirectory }, true, cancellationToken)
            .ConfigureAwait(false);
        return selections is [var selection] ? new(selection.Path, selection.Access) : null;
    }

    public async ValueTask<IReadOnlyList<NativeDirectorySelection>?> SelectDirectoriesAsync(string? initialDirectory, CancellationToken cancellationToken)
    {
        var selections = await SelectCoreAsync(new NativeFileDialogRequest(false, null) { InitialDirectory = initialDirectory, AllowMultiple = true }, true,
            cancellationToken).ConfigureAwait(false);
        return selections is null ? null : [.. selections.Select(static selection => new NativeDirectorySelection(selection.Path, selection.Access))];
    }

    // Where a uniform type identifier for a filter comes from.
    internal enum ContentTypeSource { Extension, MimeType, Identifier }
    internal readonly record struct ContentType(ContentTypeSource Source, string Value);

    // The types a panel allows for some filters: their extensions and MIME types, or null for every file.
    internal static ContentType[]? AllowedTypes(IEnumerable<FileDialogFilter> filters)
    {
        List<ContentType> types = [];
        foreach (var filter in filters)
        {
            if (filter.MatchesAllFiles) return null;
            foreach (string extension in filter.Extensions) types.Add(new(ContentTypeSource.Extension, extension.ToLowerInvariant()));
            foreach (string mimeType in filter.MimeTypes)
            {
                string type = mimeType.ToLowerInvariant();
                // UTType has no MIME wildcard; a top-level type maps to its abstract identifier, others are skipped.
                if (!type.EndsWith("/*", StringComparison.Ordinal)) types.Add(new(ContentTypeSource.MimeType, type));
                else if (WildcardIdentifier(type[..^2]) is { } identifier) types.Add(new(ContentTypeSource.Identifier, identifier));
            }
        }
        return types.Count == 0 ? null : [.. types.Distinct()];
    }

    internal static string? WildcardIdentifier(string topLevelType) => topLevelType switch
    {
        "image" => "public.image",
        "audio" => "public.audio",
        "video" => "public.movie",
        "text" => "public.text",
        "font" => "public.font",
        "model" => "public.3d-content",
        _ => null,
    };

    // Without a pop-up the panel offers the union of every filter; with one, the chosen filter's types.
    internal static ContentType[]? AllowedTypes(ImmutableArray<FileDialogFilter> filters, bool showsChoice) =>
        filters.IsDefaultOrEmpty ? null : showsChoice ? AllowedTypes([filters[0]]) : AllowedTypes(filters);

    private async ValueTask<IReadOnlyList<NativeFileSelection>?> SelectCoreAsync(NativeFileDialogRequest request, bool directory, CancellationToken cancellationToken)
    {
        bool save = request.Save, multiple = request.AllowMultiple && !save;
        bool showsChoice = request.ShowsFilterChoice && !directory && !request.Filters.IsDefaultOrEmpty;
        FilterChoice? choice = null;
        string? suggestedName = request.SuggestedName;
        nint panel = 0;
        var result = new TaskCompletionSource<IReadOnlyList<NativeFileSelection>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        Task cancellation = Task.CompletedTask;
        try
        {
            await OnOwnerAsync(owner, window =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                panel = Send(GetClass(save ? "NSSavePanel" : "NSOpenPanel"), Sel(save ? "savePanel" : "openPanel"));
                if (panel == 0) throw new IOException("AppKit could not create a file panel.");
                Send(panel, Sel("retain"));
                if (save)
                {
                    nint name = String(suggestedName ?? "Untitled");
                    try { SendArg(panel, Sel("setNameFieldStringValue:"), name); }
                    finally { Send(name, Sel("release")); }
                }
                else
                {
                    SendArg(panel, Sel("setCanChooseDirectories:"), directory ? 1 : 0);
                    SendArg(panel, Sel("setCanChooseFiles:"), directory ? 0 : 1);
                    SendArg(panel, Sel("setAllowsMultipleSelection:"), multiple ? 1 : 0);
                }
                if (showsChoice) choice = FilterChoice.Attach(panel, request.Filters, save);
                else if (!directory && AllowedTypes(request.Filters, showsChoice: false) is { } types) SetAllowedContentTypes(panel, types);
                // A folder that does not exist leaves AppKit's own choice: the request is a hint.
                if (request.InitialDirectory is { } folder && Directory.Exists(folder))
                {
                    nint path = String(folder);
                    try { SendArg(panel, Sel("setDirectoryURL:"), FileUrl(GetClass("NSURL"), Sel("fileURLWithPath:isDirectory:"), path, 1)); }
                    finally { Send(path, Sel("release")); }
                }
                nint block = CompletionBlock.Create(response =>
                {
                    using var pool = new AutoreleasePool();
                    List<MacOsSecurityAccess> acquired = [];
                    try
                    {
                        if (response != 1) { result.TrySetResult(null); return; }
                        nint[] urls;
                        if (multiple)
                        {
                            nint array = Send(panel, Sel("URLs"));
                            nuint count = array == 0 ? 0 : (nuint)Send(array, Sel("count"));
                            urls = new nint[(int)count];
                            for (int index = 0; index < urls.Length; index++) urls[index] = SendIndex(array, Sel("objectAtIndex:"), (nuint)index);
                        }
                        else urls = [Send(panel, Sel("URL"))];
                        if (urls.Length == 0 || urls.Contains(0)) throw new IOException("AppKit returned no selected URL.");
                        // A sandbox's user-selected grant authorizes the selected URL,
                        // not arbitrary sibling staging files. Never invent that access.
                        bool sandboxed = Sandboxed.Value;
                        var selections = new List<NativeFileSelection>(urls.Length);
                        foreach (nint url in urls)
                        {
                            var access = MacOsSecurityAccess.Acquire(url);
                            acquired.Add(access);
                            string path = ReadExactPath(Send(url, Sel("fileSystemRepresentation")));
                            selections.Add(new(path, access, !sandboxed && !access.Started));
                        }
                        if (choice?.Selected is { } chosen) selections[0] = selections[0] with { SelectedFilter = chosen };
                        if (result.TrySetResult(selections)) acquired.Clear();
                    }
                    catch (Exception error) { result.TrySetException(error); }
                    finally { foreach (var access in acquired) access.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                });
                try { BeginSheet(panel, Sel("beginSheetModalForWindow:completionHandler:"), window, block); }
                finally { CompletionBlock.Release(block); }
                Shown.TrySetResult();
            }, cancellationToken).ConfigureAwait(false);
            registration = cancellationToken.Register(() => cancellation = CancelAsync());
            async Task CancelAsync()
            {
                try
                {
                    await MacOsMainQueue.InvokeAsync(() =>
                    {
                        using var pool = new AutoreleasePool();
                        if (!result.Task.IsCompleted) SendArg(panel, Sel("cancel:"), 0);
                    }).ConfigureAwait(false);
                }
                catch (Exception error) { result.TrySetException(error); }
            }
            return await result.Task.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            await cancellation.ConfigureAwait(false);
            if (panel != 0 || choice is not null)
                await MacOsMainQueue.InvokeAsync(() =>
                {
                    using var pool = new AutoreleasePool();
                    choice?.Dispose();
                    if (panel != 0) Send(panel, Sel("release"));
                }).ConfigureAwait(false);
        }
    }

    private static string ReadExactPath(nint bytes)
    {
        if (bytes == 0) throw new IOException("AppKit returned no local path.");
        int length = 0;
        while (Marshal.ReadByte(bytes, length) != 0)
            if (++length > 1024 * 1024) throw new IOException("The native path exceeds the supported bound.");
        var value = new byte[length];
        Marshal.Copy(bytes, value, 0, length);
        try { return new UTF8Encoding(false, true).GetString(value); }
        catch (DecoderFallbackException error) { throw new IOException("The native path cannot be represented exactly in C#.", error); }
    }

    // Retain the actual NSURL. Balance only the access explicitly started here;
    // false can also mean an ordinary non-scoped URL. Actual file IO checks access.
    internal sealed class MacOsSecurityAccess(nint url, bool started) : IAsyncDisposable
    {
        private TaskCompletionSource? _closed;
        internal bool Started => started;
        internal static MacOsSecurityAccess Acquire(nint url)
        {
            Send(url, Sel("retain"));
            return new(url, SendBool(url, Sel("startAccessingSecurityScopedResource")) != 0);
        }
        public ValueTask DisposeAsync()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _closed, completion, null) is { } existing) return new(existing.Task);
            _ = ReleaseAsync(completion);
            return new(completion.Task);
        }
        private async Task ReleaseAsync(TaskCompletionSource completion)
        {
            try
            {
                await MacOsMainQueue.InvokeAsync(() =>
                {
                    using var pool = new AutoreleasePool();
                    if (started) Send(url, Sel("stopAccessingSecurityScopedResource"));
                    Send(url, Sel("release"));
                }).ConfigureAwait(false);
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }
    }

    // Real Objective-C block copy/dispose ownership. AppKit copies the block;
    // each heap copy owns a GCHandle and releases it when AppKit releases it.
    private static unsafe partial class CompletionBlock
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Block { internal nint Isa; internal int Flags, Reserved; internal nint Invoke, Descriptor, Context; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Descriptor { internal nuint Reserved, Size; internal nint Copy, Dispose, Signature; }
        private static readonly nint SystemLibrary = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        private static readonly nint StackBlock = NativeLibrary.GetExport(SystemLibrary, "_NSConcreteStackBlock");
        // Process-lifetime ABI descriptor; no per-dialog native allocation retained.
        private static readonly Descriptor* Description = CreateDescriptor();
        private static Descriptor* CreateDescriptor()
        {
            var descriptor = (Descriptor*)NativeMemory.AllocZeroed((nuint)sizeof(Descriptor));
            descriptor->Size = (nuint)sizeof(Block);
            descriptor->Copy = (nint)(delegate* unmanaged[Cdecl]<Block*, Block*, void>)&Copy;
            descriptor->Dispose = (nint)(delegate* unmanaged[Cdecl]<Block*, void>)&Dispose;
            descriptor->Signature = Marshal.StringToCoTaskMemUTF8("v@?q");
            return descriptor;
        }
        internal static nint Create(Action<long> callback)
        {
            var context = GCHandle.Alloc(callback);
            try
            {
                Block stack = new() { Isa = StackBlock, Flags = (1 << 25) | (1 << 30),
                    Invoke = (nint)(delegate* unmanaged[Cdecl]<Block*, long, void>)&Invoke,
                    Descriptor = (nint)Description, Context = GCHandle.ToIntPtr(context) };
                nint copy = BlockCopy((nint)(&stack));
                return copy != 0 ? copy : throw new InvalidOperationException("Could not retain AppKit completion block.");
            }
            finally { context.Free(); }
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Copy(Block* destination, Block* source) => destination->Context =
            GCHandle.ToIntPtr(GCHandle.Alloc(GCHandle.FromIntPtr(source->Context).Target));
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Dispose(Block* block) => GCHandle.FromIntPtr(block->Context).Free();
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Invoke(Block* block, long response) => ((Action<long>)GCHandle.FromIntPtr(block->Context).Target!)(response);
        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_copy")] private static partial nint BlockCopy(nint block);
        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_release")] internal static partial void Release(nint block);
    }
    private static ValueTask OnOwnerAsync(INativeWindowOwner owner, Action<nint> action, CancellationToken cancellationToken = default) =>
        owner.InvokeAsync(window => { using var pool = new AutoreleasePool(); action(window); }, cancellationToken);

    private sealed class AutoreleasePool : IDisposable
    {
        private readonly nint _pool = Send(Send(GetClass("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        public void Dispose() => Send(_pool, Sel("drain"));
    }

    private static bool IsSandboxed()
    {
        nint task = SecTaskCreateFromSelf(0);
        if (task == 0) return true; // Unknown entitlement state cannot authorize sibling writes.
        nint key = String("com.apple.security.app-sandbox");
        try
        {
            nint value = SecTaskCopyValueForEntitlement(task, key, out nint error);
            try { return error != 0 || value != 0 && CFBooleanGetValue(value) != 0; }
            finally { if (value != 0) CFRelease(value); if (error != 0) CFRelease(error); }
        }
        finally { Send(key, Sel("release")); CFRelease(task); }
    }
    // allowedContentTypes from UTType (macOS 11 and later). A type UTType does not know is skipped; when none is known,
    // the panel stays unrestricted rather than allowing nothing.
    private static void SetAllowedContentTypes(nint panel, ContentType[]? types)
    {
        nint array = CreateTypeArray(types);
        if (array == 0) return;
        try { SendArg(panel, Sel("setAllowedContentTypes:"), array); }
        finally { Send(array, Sel("release")); }
    }

    // A retained NSArray of UTType, empty for every file; 0 without UniformTypeIdentifiers.
    private static nint CreateTypeArray(ContentType[]? types)
    {
        if (UniformTypes.Value == 0) return 0;
        nint typeClass = GetClass("UTType");
        if (typeClass == 0) return 0;
        nint array = Send(Send(GetClass("NSMutableArray"), Sel("alloc")), Sel("init"));
        foreach (var type in types ?? [])
        {
            nint value = String(type.Value);
            try
            {
                nint uniformType = SendArgResult(typeClass, Sel(type.Source switch
                {
                    ContentTypeSource.Extension => "typeWithFilenameExtension:",
                    ContentTypeSource.MimeType => "typeWithMIMEType:",
                    _ => "typeWithIdentifier:",
                }), value);
                if (uniformType != 0) SendArg(array, Sel("addObject:"), uniformType);
            }
            finally { Send(value, Sel("release")); }
        }
        // Some requested types, none known: allow every file rather than none.
        if (types is { Length: > 0 } && Send(array, Sel("count")) == 0) { Send(array, Sel("release")); return 0; }
        return array;
    }

    // The filter pop-up: an NSPopUpButton accessory whose action swaps the panel's allowedContentTypes. Created, used
    // and disposed on the main thread.
    internal sealed class FilterChoice : IDisposable
    {
        private static readonly Dictionary<nint, FilterChoice> Choices = [];
        private static readonly Lazy<nint> TargetClass = new(RegisterTargetClass);
        private readonly nint _panel, _popUp, _target;
        private readonly nint[] _types;
        private readonly ImmutableArray<FileDialogFilter> _filters;

        private FilterChoice(nint panel, nint popUp, nint target, nint[] types, ImmutableArray<FileDialogFilter> filters) =>
            (_panel, _popUp, _target, _types, _filters) = (panel, popUp, target, types, filters);

        // The filter the pop-up shows.
        internal FileDialogFilter? Selected => (long)Send(_popUp, Sel("indexOfSelectedItem")) is var index && index >= 0 && index < _filters.Length
            ? _filters[(int)index] : null;

        internal static FilterChoice Attach(nint panel, ImmutableArray<FileDialogFilter> filters, bool save)
        {
            nint popUp = PopUp(Send(GetClass("NSPopUpButton"), Sel("alloc")), Sel("initWithFrame:pullsDown:"), new Rect(0, 0, 240, 26), 0);
            if (popUp == 0) throw new IOException("AppKit could not create the filter pop-up.");
            nint menu = Send(popUp, Sel("menu"));
            nint empty = String("");
            try
            {
                // Through the menu, so that two filters with the same name both stay; addItemWithTitle: keeps one.
                foreach (var filter in filters)
                {
                    nint title = String(filter.Name);
                    try { AddItem(menu, Sel("addItemWithTitle:action:keyEquivalent:"), title, 0, empty); }
                    finally { Send(title, Sel("release")); }
                }
            }
            finally { Send(empty, Sel("release")); }
            SendIndex(popUp, Sel("selectItemAtIndex:"), 0);
            Send(popUp, Sel("sizeToFit"));
            nint target = Send(Send(TargetClass.Value, Sel("alloc")), Sel("init"));
            SendArg(popUp, Sel("setTarget:"), target);
            SendArg(popUp, Sel("setAction:"), Sel("runicFilterChanged:"));
            nint[] types = [.. filters.Select(static filter => CreateTypeArray(AllowedTypes([filter])))];
            var choice = new FilterChoice(panel, popUp, target, types, filters);
            lock (Choices) Choices[popUp] = choice;
            SendArg(panel, Sel("setAccessoryView:"), popUp);
            if (!save) SendArg(panel, Sel("setAccessoryViewDisclosed:"), 1);
            choice.Apply();
            return choice;
        }

        // Selects a filter as the user would and runs the pop-up's action.
        private void Choose(int index)
        {
            SendIndex(_popUp, Sel("selectItemAtIndex:"), (nuint)index);
            SendAction(_popUp, Sel("sendAction:to:"), Sel("runicFilterChanged:"), _target);
        }

        // The number of types the panel allows now; 0 for every file.
        private long AllowedTypeCount => Send(Send(_panel, Sel("allowedContentTypes")), Sel("count"));

        // For the native tests: attaches a pop-up to an open panel that is never shown, then chooses each filter in turn
        // and reports how many types the panel allowed and which filter the pop-up reported.
        internal static ValueTask<(long Count, string? Selected)[]> InspectAsync(ImmutableArray<FileDialogFilter> filters)
        {
            (long, string?)[] observed = new (long, string?)[filters.Length];
            return Await(MacOsMainQueue.InvokeAsync(() =>
            {
                using var pool = new AutoreleasePool();
                nint panel = Send(GetClass("NSOpenPanel"), Sel("openPanel"));
                if (panel == 0) throw new IOException("AppKit could not create a file panel.");
                Send(panel, Sel("retain"));
                try
                {
                    using var choice = Attach(panel, filters, save: false);
                    for (int index = filters.Length - 1; index >= 0; index--)
                    {
                        choice.Choose(index);
                        observed[index] = (choice.AllowedTypeCount, choice.Selected?.Name);
                    }
                }
                finally { Send(panel, Sel("release")); }
            }), observed);

            static async ValueTask<(long, string?)[]> Await(ValueTask work, (long, string?)[] result) { await work.ConfigureAwait(false); return result; }
        }

        private void Apply()
        {
            nint index = Send(_popUp, Sel("indexOfSelectedItem"));
            if (index < 0 || index >= _types.Length) return;
            // An empty array allows every file, as does a filter none of whose types UTType knows.
            nint types = _types[index];
            if (types != 0) SendArg(_panel, Sel("setAllowedContentTypes:"), types);
            else
            {
                nint all = Send(GetClass("NSArray"), Sel("array"));
                SendArg(_panel, Sel("setAllowedContentTypes:"), all);
            }
        }

        public void Dispose()
        {
            lock (Choices) Choices.Remove(_popUp);
            SendArg(_popUp, Sel("setTarget:"), 0);
            Send(_target, Sel("release"));
            Send(_popUp, Sel("release"));
            foreach (nint types in _types) if (types != 0) Send(types, Sel("release"));
        }

        private static unsafe nint RegisterTargetClass()
        {
            const string name = "RunicPlatformFileFilterTarget";
            nint existing = GetClass(name);
            if (existing != 0) return existing;
            nint type = AllocateClassPair(GetClass("NSObject"), name, 0);
            if (type == 0) throw new IOException("AppKit could not register the filter pop-up target.");
            delegate* unmanaged[Cdecl]<nint, nint, nint, void> changed = &Changed;
            if (AddMethod(type, Sel("runicFilterChanged:"), (nint)changed, "v@:@") == 0)
                throw new IOException("AppKit could not register the filter pop-up action.");
            RegisterClassPair(type);
            return type;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Changed(nint self, nint selector, nint sender)
        {
            try
            {
                FilterChoice? choice;
                lock (Choices) Choices.TryGetValue(sender, out choice);
                choice?.Apply();
            }
            catch (Exception) { } // An exception must not unwind into AppKit; the panel keeps its previous types.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Rect(double X, double Y, double Width, double Height);

    private static readonly Lazy<nint> UniformTypes = new(static () =>
        NativeLibrary.TryLoad("/System/Library/Frameworks/UniformTypeIdentifiers.framework/UniformTypeIdentifiers", out nint library) ? library : 0);
    private static nint String(string value) => StringSend(Send(GetClass("NSString"), Sel("alloc")), Sel("initWithUTF8String:"), value);
    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")] private static partial nint SecTaskCreateFromSelf(nint allocator);
    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")] private static partial nint SecTaskCopyValueForEntitlement(nint task, nint entitlement, out nint error);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static partial byte CFBooleanGetValue(nint value);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static partial void CFRelease(nint value);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint GetClass(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial nint Send(nint receiver, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial byte SendBool(nint receiver, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial void SendArg(nint receiver, nint selector, nint argument);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial nint SendArgResult(nint receiver, nint selector, nint argument);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial nint SendIndex(nint receiver, nint selector, nuint index);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial nint FileUrl(nint receiver, nint selector, nint path, byte isDirectory);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial void BeginSheet(nint receiver, nint selector, nint window, nint completion);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)] private static partial nint StringSend(nint receiver, nint selector, string value);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial nint PopUp(nint receiver, nint selector, Rect frame, byte pullsDown);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial nint AddItem(nint receiver, nint selector, nint title, nint action, nint keyEquivalent);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static partial byte SendAction(nint receiver, nint selector, nint action, nint target);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)] private static partial nint AllocateClassPair(nint superclass, string name, nuint extraBytes);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)] private static partial byte AddMethod(nint type, nint selector, nint implementation, string types);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_registerClassPair")] private static partial void RegisterClassPair(nint type);
}
