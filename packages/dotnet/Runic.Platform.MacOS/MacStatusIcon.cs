using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// An NSStatusItem from NSStatusBar.systemStatusBar. AppKit objects are created, changed and released on the main
// thread, which only the Desktop event loop services: without it, requests report BackendUnavailable after
// MainThreadTimeout. The menu is built from the state and opened on demand: an item with an assigned menu opens it
// on every click and never sends the button's action.
internal sealed partial class MacStatusIcon : IDesktopStatusIcon
{
    private const double VariableLength = -1;
    private const long NoImage = 0, ImageOnly = 1, StateOff = 0, StateOn = 1;
    private static readonly ConcurrentDictionary<nint, MacStatusIcon> Owners = new();
    private static readonly Lazy<nint> TargetClass = new(CreateTargetClass);
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Activations reach the application one at a time, in order, and never after disposal.
    private readonly Channel<DesktopStatusIconActivation> _activations =
        Channel.CreateUnbounded<DesktopStatusIconActivation>(new() { SingleReader = true });
    private Task? _pump;
    // Whether a request reached the main queue, so later ones queue their own cleanup behind it.
    private volatile bool _queued, _disposed;
    // Main thread only.
    private nint _item, _target, _menu;
    private DesktopStatusIconState? _state;
    private MacStatusMenu? _openMenu;
    // While the menu is open, AppKit uses the item and the menu, and does not run the main queue: requests wait
    // until it closes, and removal and release wait for performClick: to return.
    private volatile bool _tracking;
    private bool _removeAfterMenu, _releaseAfterMenu;

    /// <summary>How long a request waits for the main thread. Only the Desktop event loop services it.</summary>
    internal static TimeSpan MainThreadTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public event EventHandler<DesktopStatusIconActivation>? Activated;

    public async ValueTask<PlatformResult<PlatformUnit>> ShowAsync(DesktopStatusIconState state, CancellationToken cancellationToken = default)
    {
        DesktopServiceValidation.StatusIcon(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pump ??= Task.Run(PumpAsync, CancellationToken.None);
            PlatformResult<PlatformUnit> result = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            _queued = true;
            // A show the caller gave up on does not run later, so it never undoes a later hide.
            await OnMainThreadAsync(() => result = Show(state), abandonable: true, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<PlatformResult<PlatformUnit>> HideAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Queued behind every earlier request, which the main queue runs in order. It stays queued when the wait
            // ends, so the item goes once the event loop runs again. An open menu closes first.
            CloseOpenMenu();
            if (_queued && !await OnMainThreadAsync(Remove, abandonable: false, cancellationToken).ConfigureAwait(false))
                return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        // Requests wait at most MainThreadTimeout, so the gate is free by then.
        bool entered = await _gate.WaitAsync(MainThreadTimeout + MainThreadTimeout).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _activations.Writer.TryComplete();
            // Without an event loop the item ends with the process.
            CloseOpenMenu();
            if (_queued) await OnMainThreadAsync(ReleaseAll, abandonable: false, CancellationToken.None).ConfigureAwait(false);
        }
        finally { if (entered) _gate.Release(); }
    }

    // Runs work on the main thread and reports whether it ran within MainThreadTimeout. Abandonable work that has not
    // started when the wait ends never runs; other work stays queued.
    private static async Task<bool> OnMainThreadAsync(Action work, bool abandonable, CancellationToken cancellationToken)
    {
        var state = new StrongBox<int>(); // 0 queued, 1 started, 2 abandoned
        var queued = MacOsMainQueue.InvokeAsync(() =>
        {
            if (abandonable && Interlocked.CompareExchange(ref state.Value, 1, 0) != 0) return;
            work();
        }).AsTask();
        try
        {
            await queued.WaitAsync(MainThreadTimeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            if (abandonable && Interlocked.CompareExchange(ref state.Value, 2, 0) == 1)
            {
                // It started on the main thread, which finishes it promptly.
                await queued.ConfigureAwait(false);
                return true;
            }
            if (error is OperationCanceledException) throw;
            return false;
        }
    }

    private PlatformResult<PlatformUnit> Show(DesktopStatusIconState state)
    {
        if (_disposed || !MacAppKit.IsAvailable) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        using var pool = new Pool();
        MacAppKit.EnsureApplication();
        _removeAfterMenu = false;
        if (_target == 0)
        {
            _target = Send(Send(TargetClass.Value, Sel("alloc")), Sel("init"));
            Owners[_target] = this;
        }
        if (_item == 0)
        {
            var item = ArgDouble(Send(Class("NSStatusBar"), Sel("systemStatusBar")), Sel("statusItemWithLength:"), VariableLength);
            if (item == 0) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            _item = Send(item, Sel("retain"));
        }
        var button = Send(_item, Sel("button"));
        if (button == 0) { Remove(); return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
        MacStatusButton.Adopt(button);
        var image = CreateImage(MacStatusIconModel.ShownIcon(state));
        try
        {
            Arg(button, Sel("setImage:"), image);
            // Without an image, as for an icon that names only a theme icon, the menu bar shows the title.
            Arg(button, Sel("setTitle:"), String(image == 0 ? state.Title : ""));
            Arg(button, Sel("setImagePosition:"), (nint)(image == 0 ? NoImage : ImageOnly));
        }
        finally { Release(image); }
        Arg(button, Sel("setToolTip:"), String(MacStatusIconModel.ToolTip(state)));
        Arg(button, Sel("setAccessibilityLabel:"), String(state.Title));
        Arg(button, Sel("setTarget:"), _target);
        Arg(button, Sel("setAction:"), Sel("runicStatusItemClicked:"));
        Arg(button, Sel("sendActionOn:"), unchecked((nint)MacStatusIconModel.ActionMask));
        _state = state;
        return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    }

    // Main thread only. While the menu is open, closes it and removes the item once AppKit is done with it.
    private void Remove()
    {
        if (_tracking)
        {
            _removeAfterMenu = true;
            Send(_menu, Sel("cancelTracking"));
            return;
        }
        if (_item != 0)
        {
            using var pool = new Pool();
            Arg(Send(Class("NSStatusBar"), Sel("systemStatusBar")), Sel("removeStatusItem:"), _item);
            Release(_item);
            _item = 0;
        }
        Release(_menu);
        _menu = 0;
        _openMenu = null;
    }

    // Main thread only: removes the item and releases the action target, after the menu closes if it is open.
    private void ReleaseAll()
    {
        Remove();
        if (_tracking) { _releaseAfterMenu = true; return; }
        if (_target != 0) { Owners.TryRemove(_target, out _); Release(_target); _target = 0; }
    }

    // An NSImage with one representation per image, all the same size in points, so AppKit picks the one that suits
    // the display's scale. Null without images. The caller releases it.
    private static unsafe nint CreateImage(DesktopIcon icon)
    {
        if (icon.Images.Length == 0) return 0;
        var (width, height) = MacStatusIconModel.PointSize(icon);
        var size = new Size(width, height);
        var image = ArgSize(Send(Class("NSImage"), Sel("alloc")), Sel("initWithSize:"), size);
        if (image == 0) return 0;
        int added = 0;
        foreach (var source in icon.Images)
        {
            var png = MacStatusIconModel.EncodePng(source);
            nint data;
            fixed (byte* bytes = png) data = Bytes(Class("NSData"), Sel("dataWithBytes:length:"), bytes, (nuint)png.Length);
            var representation = Arg(Class("NSBitmapImageRep"), Sel("imageRepWithData:"), data);
            if (representation == 0) continue;
            ArgSize(representation, Sel("setSize:"), size);
            Arg(image, Sel("addRepresentation:"), representation);
            added++;
        }
        if (added == 0) { Release(image); return 0; }
        Arg(image, Sel("setTemplate:"), MacStatusIconModel.IsTemplate(icon) ? 1 : 0);
        return image;
    }

    private void Clicked(nint button)
    {
        if (_item == 0 || _state is not { } state) return;
        // A press from VoiceOver or performClick: has no mouse event; the current event is then an older one.
        var currentEvent = Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("currentEvent"));
        long type = currentEvent == 0 ? 0 : Send(currentEvent, Sel("type"));
        bool fromButton = currentEvent != 0 && MacStatusIconModel.IsMouseUp(type)
            && Send(currentEvent, Sel("window")) == Send(button, Sel("window"));
        ulong flags = fromButton ? unchecked((ulong)Send(currentEvent, Sel("modifierFlags"))) : 0;
        switch (MacStatusIconModel.Classify(fromButton ? type : 0, flags, state.Menu.Length > 0))
        {
            case MacStatusClick.Primary: Raise(DesktopStatusIconActivationKind.Primary); break;
            case MacStatusClick.Secondary: Raise(DesktopStatusIconActivationKind.Secondary); break;
            case MacStatusClick.Menu: OpenMenu(state); break;
        }
    }

    // Opens the menu for a right click or VoiceOver's show-menu action: assigned to the item, opened with
    // performClick:, which returns once the menu closes, and taken back at once, so the next click sends the
    // button's action again. menuDidClose: takes it back too, should AppKit return earlier. The menu stays retained
    // until the next one, because AppKit sends the chosen item's action after the menu closes.
    private bool OpenMenu(DesktopStatusIconState state)
    {
        if (_tracking || _item == 0 || state.Menu.Length == 0) return false;
        BuildMenu(state);
        Arg(_menu, Sel("setDelegate:"), _target);
        Arg(_item, Sel("setMenu:"), _menu);
        _tracking = true;
        try { Arg(Send(_item, Sel("button")), Sel("performClick:"), 0); }
        finally
        {
            _tracking = false;
            if (_item != 0) Arg(_item, Sel("setMenu:"), 0);
            if (_releaseAfterMenu) { _releaseAfterMenu = false; _removeAfterMenu = false; ReleaseAll(); }
            else if (_removeAfterMenu) { _removeAfterMenu = false; Remove(); }
        }
        return true;
    }

    // VoiceOver's show-menu action on the button (MacStatusButton).
    internal static bool ShowMenu(nint button)
    {
        using var pool = new Pool();
        return Owners.TryGetValue(Send(button, Sel("target")), out var icon) && icon._state is { } state && icon.OpenMenu(state);
    }

    private void BuildMenu(DesktopStatusIconState state)
    {
        Release(_menu);
        _openMenu = MacStatusMenu.Build(state.Menu);
        _menu = CreateMenu(_openMenu.Entries);
    }

    private nint CreateMenu(IEnumerable<MacMenuEntry> entries)
    {
        var menu = Arg(Send(Class("NSMenu"), Sel("alloc")), Sel("initWithTitle:"), String(""));
        // Enabled states come from the application, not from AppKit's validation.
        Arg(menu, Sel("setAutoenablesItems:"), 0);
        foreach (var entry in entries)
        {
            if (entry.Kind == MacMenuEntryKind.Separator)
            {
                Arg(menu, Sel("addItem:"), Send(Class("NSMenuItem"), Sel("separatorItem")));
                continue;
            }
            bool command = entry.Kind == MacMenuEntryKind.Command;
            var item = Args3(Send(Class("NSMenuItem"), Sel("alloc")), Sel("initWithTitle:action:keyEquivalent:"),
                String(entry.Label), command ? Sel("runicStatusMenuItem:") : 0, String(""));
            if (command)
            {
                Arg(item, Sel("setTarget:"), _target);
                Arg(item, Sel("setTag:"), entry.Tag);
                if (entry.Checked is { } isChecked) Arg(item, Sel("setState:"), (nint)(isChecked ? StateOn : StateOff));
            }
            else
            {
                var submenu = CreateMenu(entry.Children);
                Arg(item, Sel("setSubmenu:"), submenu);
                Release(submenu);
            }
            Arg(item, Sel("setEnabled:"), entry.Enabled ? 1 : 0);
            Arg(menu, Sel("addItem:"), item);
            Release(item);
        }
        return menu;
    }

    private void Raise(DesktopStatusIconActivationKind kind, string? commandId = null, string? commandParameter = null)
    {
        if (!_disposed) _activations.Writer.TryWrite(new DesktopStatusIconActivation(kind, commandId) { CommandParameter = commandParameter });
    }

    private async Task PumpAsync()
    {
        await foreach (var activation in _activations.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_disposed) return;
            try { Activated?.Invoke(this, activation); }
            catch (Exception error)
            {
                // As with an event raised on the thread pool, a throwing handler fails the process; later activations still arrive until then.
                var capture = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                ThreadPool.QueueUserWorkItem(static state => ((System.Runtime.ExceptionServices.ExceptionDispatchInfo)state!).Throw(), capture);
            }
        }
    }

    private static unsafe nint CreateTargetClass()
    {
        const string Name = "RunicStatusItemTarget";
        var existing = Class(Name);
        if (existing != 0) return existing;
        var type = AllocateClass(Class("NSObject"), Name, 0);
        if (type == 0) throw new InvalidOperationException("The status item target class could not be created.");
        AddMethod(type, Sel("runicStatusItemClicked:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnClicked, "v@:@");
        AddMethod(type, Sel("runicStatusMenuItem:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMenuItem, "v@:@");
        AddMethod(type, Sel("menuWillOpen:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMenuWillOpen, "v@:@");
        AddMethod(type, Sel("menuDidClose:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMenuDidClose, "v@:@");
        RegisterClass(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClicked(nint self, nint selector, nint sender)
    {
        try
        {
            using var pool = new Pool();
            if (Owners.TryGetValue(self, out var icon)) icon.Clicked(sender);
        }
        catch { /* Never unwind through AppKit. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMenuItem(nint self, nint selector, nint sender)
    {
        try
        {
            var tag = Send(sender, Sel("tag"));
            if (Owners.TryGetValue(self, out var icon) && icon._openMenu is { } menu && menu.CommandFor(tag) is { } id)
                icon.Raise(DesktopStatusIconActivationKind.MenuItem, id, menu.ParameterFor(tag));
        }
        catch { /* Never unwind through AppKit. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMenuWillOpen(nint self, nint selector, nint menu)
    {
        if (Owners.TryGetValue(self, out var icon)) icon._menuOpenings++;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMenuDidClose(nint self, nint selector, nint menu)
    {
        try
        {
            if (Owners.TryGetValue(self, out var icon) && icon._item != 0 && Send(icon._item, Sel("menu")) == menu)
                Arg(icon._item, Sel("setMenu:"), 0);
        }
        catch { /* Never unwind through AppKit. */ }
    }

    // What the item shows, for the native tests.
    internal readonly record struct Snapshot(bool Shown, bool HasImage, bool Template, int Representations, string Title, string ToolTip, string AccessibilityLabel);

    internal async ValueTask<Snapshot> InspectAsync()
    {
        Snapshot snapshot = default;
        await MacOsMainQueue.InvokeAsync(() =>
        {
            using var pool = new Pool();
            if (_item == 0) return;
            var button = Send(_item, Sel("button"));
            // The image the button draws: NSNoImage hides an image the button keeps after setImage: with nil.
            var image = Send(button, Sel("imagePosition")) == NoImage ? 0 : Send(button, Sel("image"));
            snapshot = new Snapshot(true, image != 0, image != 0 && Bool(image, Sel("isTemplate")) != 0,
                image == 0 ? 0 : (int)Send(Send(image, Sel("representations")), Sel("count")),
                Text(Send(button, Sel("title"))), Text(Send(button, Sel("toolTip"))), Text(Send(button, Sel("accessibilityLabel"))));
        }).ConfigureAwait(false);
        return snapshot;
    }

    // Main thread only: how often the menu opened, for the native tests.
    private int _menuOpenings;

    // Presses the button as VoiceOver does (accessibilityPerformPress), or with performClick:.
    internal ValueTask PressAsync(bool accessibility) => MacOsMainQueue.InvokeAsync(() =>
    {
        using var pool = new Pool();
        if (_item == 0) return;
        var button = Send(_item, Sel("button"));
        if (accessibility) Bool(button, Sel("accessibilityPerformPress"));
        else Arg(button, Sel("performClick:"), 0);
    });

    // VoiceOver's show-menu action. It returns once the menu closes; whileOpen runs on a worker thread while it is
    // open, when the main queue waits. Reports whether the action was handled, whether the menu opened, and whether
    // the item still holds the menu afterwards.
    internal async ValueTask<(bool Handled, bool Opened, bool MenuKept)> ShowMenuAsync(Func<ValueTask> whileOpen)
    {
        int before = 0;
        bool handled = false, kept = false;
        await MacOsMainQueue.InvokeAsync(() => before = _menuOpenings).ConfigureAwait(false);
        var opening = MacOsMainQueue.InvokeAsync(() =>
        {
            using var pool = new Pool();
            if (_item == 0) return;
            handled = Bool(Send(_item, Sel("button")), Sel("accessibilityPerformShowMenu")) != 0;
            kept = _item != 0 && Send(_item, Sel("menu")) != 0;
        }).AsTask();
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await whileOpen().ConfigureAwait(false);
        try { await opening.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Leave no menu open, which would keep the main thread from the rest of the process.
            CloseOpenMenu();
            throw;
        }
        int after = 0;
        await MacOsMainQueue.InvokeAsync(() => after = _menuOpenings).ConfigureAwait(false);
        return (handled, after > before, kept);
    }

    // Closes an open menu, as Escape does.
    internal void CloseMenu() => CloseOpenMenu();

    // The main queue waits while the menu is open, so an Escape key press is posted to the application's event queue,
    // which postEvent:atStart: accepts from any thread and the menu reads.
    private void CloseOpenMenu()
    {
        if (!_tracking) return;
        using var pool = new Pool();
        var escape = CreateKeyboardEvent(0, EscapeKey, true);
        if (escape == 0) return;
        try { Args(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("postEvent:atStart:"), Arg(Class("NSEvent"), Sel("eventWithCGEvent:"), escape), 1); }
        finally { CFRelease(escape); }
    }

    private const ushort EscapeKey = 0x35;
    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", EntryPoint = "CGEventCreateKeyboardEvent")]
    private static partial nint CreateKeyboardEvent(nint source, ushort key, [MarshalAs(UnmanagedType.U1)] bool down);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
    private static partial void CFRelease(nint value);

    // Builds the menu the next right click opens, describes it and chooses the item at the index path, without
    // opening it: a menu that is open runs a modal tracking loop until the user closes it.
    internal async ValueTask<string> ChooseMenuItemAsync(params int[] path)
    {
        string description = "";
        await MacOsMainQueue.InvokeAsync(() =>
        {
            using var pool = new Pool();
            if (_state is not { } state) return;
            BuildMenu(state);
            description = Describe(_menu);
            var menu = _menu;
            for (int i = 0; i < path.Length - 1; i++) menu = Send(Arg(menu, Sel("itemAtIndex:"), path[i]), Sel("submenu"));
            Arg(menu, Sel("performActionForItemAtIndex:"), path[^1]);
        }).ConfigureAwait(false);
        return description;
    }

    private static string Describe(nint menu)
    {
        var text = new StringBuilder();
        long count = Send(menu, Sel("numberOfItems"));
        for (long i = 0; i < count; i++)
        {
            var item = Arg(menu, Sel("itemAtIndex:"), (nint)i);
            if (i > 0) text.Append(';');
            if (Bool(item, Sel("isSeparatorItem")) != 0) { text.Append('-'); continue; }
            text.Append(Text(Send(item, Sel("title"))));
            if (Bool(item, Sel("isEnabled")) == 0) text.Append("[disabled]");
            if (Send(item, Sel("state")) == StateOn) text.Append("[on]");
            if (Send(item, Sel("submenu")) is var submenu and not 0) text.Append('(').Append(Describe(submenu)).Append(')');
        }
        return text.ToString();
    }

    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)] private static partial nint AllocateClass(nint parent, string name, nuint extra);
    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")] private static partial void RegisterClass(nint type);
    [LibraryImport(ObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)] private static partial byte AddMethod(nint type, nint selector, nint implementation, string types);
}
