using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// An NSStatusItem from NSStatusBar.systemStatusBar. AppKit objects are created, changed and released on the main
// thread, which the Desktop event loop services while the application runs. The menu is built from the state and
// opened on demand: an item with an assigned menu opens it on every click and never sends the button's action.
internal sealed partial class MacStatusIcon : IDesktopStatusIcon
{
    private const double VariableLength = -1;
    private const long NoImage = 0, ImageOnly = 1, StateOff = 0, StateOn = 1;
    // Without a running Desktop event loop the main queue never runs; the process is ending then.
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<nint, MacStatusIcon> Owners = new();
    private static readonly Lazy<nint> TargetClass = new(CreateTargetClass);
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Activations reach the application one at a time, in order, and never after disposal.
    private readonly Channel<DesktopStatusIconActivation> _activations =
        Channel.CreateUnbounded<DesktopStatusIconActivation>(new() { SingleReader = true });
    private Task? _pump;
    // Whether native objects exist that disposal releases on the main thread.
    private volatile bool _shown, _native, _disposed;
    // Main thread only.
    private nint _item, _target, _menu;
    private DesktopStatusIconState? _state;
    private MacStatusMenu? _openMenu;

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
            await MacOsMainQueue.InvokeAsync(() => result = Show(state)).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
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
            if (_shown) await MacOsMainQueue.InvokeAsync(Remove).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _activations.Writer.TryComplete();
            if (_native)
            {
                try
                {
                    await MacOsMainQueue.InvokeAsync(() =>
                    {
                        Remove();
                        if (_target != 0) { Owners.TryRemove(_target, out _); Release(_target); _target = 0; }
                    }).AsTask().WaitAsync(ReleaseTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException) { /* No event loop runs any more: the item ends with the process. */ }
            }
        }
        finally { _gate.Release(); }
    }

    private PlatformResult<PlatformUnit> Show(DesktopStatusIconState state)
    {
        if (_disposed || !MacAppKit.IsAvailable) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        using var pool = new Pool();
        MacAppKit.EnsureApplication();
        if (_target == 0)
        {
            _target = Send(Send(TargetClass.Value, Sel("alloc")), Sel("init"));
            Owners[_target] = this;
            _native = true;
        }
        if (_item == 0)
        {
            var item = ArgDouble(Send(Class("NSStatusBar"), Sel("systemStatusBar")), Sel("statusItemWithLength:"), VariableLength);
            if (item == 0) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            _item = Send(item, Sel("retain"));
        }
        var button = Send(_item, Sel("button"));
        if (button == 0) { Remove(); return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
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
        _shown = true;
        return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    }

    // Main thread only.
    private void Remove()
    {
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
        _shown = false;
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

    private void Clicked()
    {
        if (_item == 0 || _state is not { } state) return;
        var currentEvent = Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("currentEvent"));
        long type = currentEvent == 0 ? 0 : Send(currentEvent, Sel("type"));
        ulong flags = currentEvent == 0 ? 0 : unchecked((ulong)Send(currentEvent, Sel("modifierFlags")));
        switch (MacStatusIconModel.Classify(type, flags, state.Menu.Length > 0))
        {
            case MacStatusClick.Primary: Raise(DesktopStatusIconActivationKind.Primary); break;
            case MacStatusClick.Secondary: Raise(DesktopStatusIconActivationKind.Secondary); break;
            case MacStatusClick.Menu: OpenMenu(state); break;
        }
    }

    // Assigns the menu for one click: performClick: opens it, and menuDidClose: takes it back, so the next primary
    // click sends the button's action again. The menu stays retained until the next one, because AppKit sends the
    // chosen item's action after the menu closes.
    private void OpenMenu(DesktopStatusIconState state)
    {
        BuildMenu(state);
        Arg(_menu, Sel("setDelegate:"), _target);
        Arg(_item, Sel("setMenu:"), _menu);
        Arg(Send(_item, Sel("button")), Sel("performClick:"), 0);
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

    private void Raise(DesktopStatusIconActivationKind kind, string? menuItemId = null)
    {
        if (!_disposed) _activations.Writer.TryWrite(new DesktopStatusIconActivation(kind, menuItemId));
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
            if (Owners.TryGetValue(self, out var icon)) icon.Clicked();
        }
        catch { /* Never unwind through AppKit. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMenuItem(nint self, nint selector, nint sender)
    {
        try
        {
            if (Owners.TryGetValue(self, out var icon) && icon._openMenu?.CommandFor(Send(sender, Sel("tag"))) is { } id)
                icon.Raise(DesktopStatusIconActivationKind.MenuItem, id);
        }
        catch { /* Never unwind through AppKit. */ }
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
            var image = Send(button, Sel("image"));
            snapshot = new Snapshot(true, image != 0, image != 0 && Bool(image, Sel("isTemplate")) != 0,
                image == 0 ? 0 : (int)Send(Send(image, Sel("representations")), Sel("count")),
                Text(Send(button, Sel("title"))), Text(Send(button, Sel("toolTip"))), Text(Send(button, Sel("accessibilityLabel"))));
        }).ConfigureAwait(false);
        return snapshot;
    }

    // Sends the button's action as a press does (performClick: with no menu assigned).
    internal ValueTask PressAsync() => MacOsMainQueue.InvokeAsync(() =>
    {
        using var pool = new Pool();
        if (_item != 0) Arg(Send(_item, Sel("button")), Sel("performClick:"), 0);
    });

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
