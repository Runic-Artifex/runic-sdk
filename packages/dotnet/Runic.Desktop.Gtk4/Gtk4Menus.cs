using System.Collections.Immutable;
using System.Runtime.Versioning;
using Runic.Desktop.Internal;
using Runic.Platform;

namespace Runic.Desktop.Gtk4;

// B10: the GTK 4 window's menu bar, its shortcuts and its context menus.
// - The bar is a GtkPopoverMenuBar built from a GMenu, above the web view in a vertical GtkBox, for the Native
//   presentation of a decorated window. Runic does not use gtk_application_set_menubar, which depends on the shell
//   and would add application accels that run before the page.
// - Every item has its own action in one GSimpleActionGroup ("runic.item-N"): stateless for plain items, a boolean
//   state for check items, and an int32 state with the item's own target for radio items, so GTK draws the check or
//   radio mark from the declaration and each item has its own enabled state. Runic handles activate, so GTK never
//   changes a state itself. Actions follow CanExecute when a popover of the bar is shown.
// - The "accel" attribute shows the shortcut text without registering the key; the unhandled-key controller matches
//   it after the page (GtkMenuShortcuts).
// - A context menu is a GtkPopoverMenu on the web view pointing at the page point or the last pointer position, with
//   its own action group ("runiccontext").
[SupportedOSPlatform("linux")]
internal sealed partial class Gtk4WindowHost : IDesktopMenuWindowHost
{
    private const string BarPrefix = "runic";
    private const string ContextPrefix = "runiccontext";

    private IDesktopMenuCommandTarget? _menuCommands;
    // GTK thread only: the box holding the bar and the view (null when the window shows no bar in it), the bar and its
    // actions, the shortcuts of the declared bar, the open context menu and the last pointer position over the view.
    private Gtk.Box? _menuBox;
    private Gtk.PopoverMenuBar? _menuBar;
    private Gtk4MenuModel? _barModel;
    private readonly MenuIdAllocator _barIds = new();
    private GtkMenuShortcuts _shortcuts = GtkMenuShortcuts.Empty;
    // The planned bar, which the global menu exports too, the window's global menu, and whether it shows the bar now.
    private ImmutableArray<PlannedMenuEntry> _plan = [];
    private LinuxGlobalMenu? _globalMenu;
    private volatile bool _globalPlacement;
    private Gtk4ContextMenu? _contextMenu;
    private Gtk.EventControllerMotion? _pointerMotion;
    private (double X, double Y)? _pointer;

    public IDesktopMenuCommandTarget? MenuCommands
    {
        get => Volatile.Read(ref _menuCommands);
        set => Volatile.Write(ref _menuCommands, value);
    }

    public DesktopMenuBarPlacement MenuBarPlacement => IsOpen && _options is { } options
        ? GtkMenus.Placement(options.MenuBarPresentation, options.Frameless, options.Kiosk, _globalPlacement)
        : DesktopMenuBarPlacement.None;

    // Raised on the GTK thread when the bar goes into the desktop's global menu or comes back.
    public event EventHandler? MenuBarPlacementChanged;

    // Internal for the smokes: the bar, or null.
    internal Gtk.PopoverMenuBar? MenuBarWidget => _menuBar;

    // Internal for the smokes: the open context menu's popover, or null.
    internal Gtk.PopoverMenu? ContextMenuWidget => _contextMenu?.Popover;

    public ValueTask SetMenuBarAsync(IReadOnlyList<DesktopMenu> menus, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menus);
        if (!IsOpen) return ValueTask.FromException(new InvalidOperationException("The GTK 4 window is not open."));
        return new(Gtk4Dispatcher.Instance.InvokeAsync(() => ApplyMenuBar(menus), cancellationToken));
    }

    // The window's child: the view, or a box with the view for a window that shows its bar in the window.
    private Gtk.Widget CreateMenuContent(DesktopWindowHostOptions options, WebKit.WebView webView)
    {
        _pointerMotion = Gtk.EventControllerMotion.New();
        _pointerMotion.OnMotion += OnPointerMotion;
        webView.AddController(_pointerMotion);
        if (!GtkMenus.ShowsInWindowBar(options.MenuBarPresentation, options.Frameless, options.Kiosk)) return webView;
        _menuBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        webView.SetVexpand(true);
        webView.SetHexpand(true);
        _menuBox.Append(webView);
        return _menuBox;
    }

    private void OnPointerMotion(Gtk.EventControllerMotion controller, Gtk.EventControllerMotion.MotionSignalArgs args) =>
        _pointer = (args.X, args.Y);

    private void ApplyMenuBar(IReadOnlyList<DesktopMenu> menus)
    {
        if (!IsOpen) return;
        var plan = DesktopMenuPlanner.PlanBar(menus, MenuPlatform.Linux);
        _shortcuts = GtkMenuShortcuts.Create(plan);
        _plan = plan;
        _globalMenu?.SetMenus(plan);
        if (_menuBar is { } old)
        {
            _menuBox?.Remove(old);
            _menuBar = null;
            ReleaseWrappers(_barModel, old);
        }
        _barModel = null;
        _window?.InsertActionGroup(BarPrefix, null);
        if (_menuBox is not { } box || plan.IsEmpty) return;
        var model = new Gtk4MenuModel(BarPrefix, _barIds, context: false);
        var root = Gio.Menu.New();
        foreach (var menu in plan)
        {
            root.AppendSubmenu(menu.MnemonicLabel, BuildMenu(menu.Children, model));
        }
        _window?.InsertActionGroup(BarPrefix, model.Actions);
        var bar = Gtk.PopoverMenuBar.NewFromModel(root);
        var direction = Direction;
        if (direction is { } barDirection) bar.SetDirection(barDirection);
        foreach (var widget in Descendants(bar).ToList())
        {
            // GTK takes a widget's direction from the default, not from its parent, so the items and popovers get it too.
            if (direction is { } itemDirection) widget.SetDirection(itemDirection);
            if (widget is Gtk.Popover popover)
            {
                popover.OnShow += (_, _) => RefreshBarActions(model);
                model.Popovers.Add(popover);
            }
            else
            {
                // GirCore's wrapper holds a reference until it is disposed; the bar keeps its own widgets.
                widget.Dispose();
            }
        }
        // The global menu shows the bar: the window keeps it hidden until the global menu goes away.
        if (_globalPlacement) bar.SetVisible(false);
        box.Prepend(bar);
        _menuBar = bar;
        _barModel = model;
    }

    private Gtk.TextDirection? Direction => _options?.LayoutDirection switch
    {
        Runic.Platform.DesktopLayoutDirection.RightToLeft => Gtk.TextDirection.Rtl,
        Runic.Platform.DesktopLayoutDirection.LeftToRight => Gtk.TextDirection.Ltr,
        _ => null,
    };

    // GTK takes a widget's direction from the default, not from its parent, so the popover's items get it too.
    private static void SetDirection(Gtk.Widget widget, Gtk.TextDirection direction)
    {
        widget.SetDirection(direction);
        foreach (var child in Descendants(widget).ToList())
        {
            child.SetDirection(direction);
            child.Dispose();
        }
    }

    private static IEnumerable<Gtk.Widget> Descendants(Gtk.Widget widget)
    {
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    // A menu level as GMenu sections: a separator starts a new section, which GTK draws with a separator line.
    private Gio.Menu BuildMenu(ImmutableArray<PlannedMenuEntry> entries, Gtk4MenuModel model)
    {
        var menu = Gio.Menu.New();
        var section = Gio.Menu.New();
        var sectionItems = 0;
        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
                case PlannedMenuKind.Separator:
                    if (sectionItems > 0)
                    {
                        menu.AppendSection(null, section);
                        section = Gio.Menu.New();
                        sectionItems = 0;
                    }
                    continue;
                case PlannedMenuKind.Menu:
                    // GMenu has no disabled submenu: a disabled menu opens with every entry disabled (planner).
                    section.AppendSubmenu(entry.MnemonicLabel, BuildMenu(entry.Children, model));
                    break;
                default:
                    if (entry.Item is { } item) section.AppendItem(BuildItem(entry, item, model));
                    break;
            }
            sectionItems++;
        }
        if (sectionItems > 0) menu.AppendSection(null, section);
        return menu;
    }

    private Gio.MenuItem BuildItem(PlannedMenuEntry entry, DesktopMenuItem item, Gtk4MenuModel model)
    {
        var id = model.Ids[entry.Key];
        var name = "item-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var action = entry.Kind switch
        {
            PlannedMenuKind.Check => Gio.SimpleAction.NewStateful(name, null, GLib.Variant.NewBoolean(item.IsChecked == true)),
            // A radio item is checked while its action's state equals its target.
            PlannedMenuKind.Radio => Gio.SimpleAction.NewStateful(name, GLib.VariantType.New("i"), GLib.Variant.NewInt32(item.IsChecked == true ? id : -1)),
            _ => Gio.SimpleAction.New(name, null),
        };
        action.SetEnabled(entry.Enabled);
        action.OnActivate += (_, _) => OnMenuAction(model, entry);
        model.Actions.AddAction(action);
        model.Items.Add((action, entry));
        var menuItem = Gio.MenuItem.New(entry.MnemonicLabel, null);
        menuItem.SetActionAndTargetValue(model.Prefix + "." + name, entry.Kind == PlannedMenuKind.Radio ? GLib.Variant.NewInt32(id) : null);
        if (entry.Shortcut is { GdkKeyval: { } keyval } shortcut)
            menuItem.SetAttributeValue("accel", GLib.Variant.NewString(Gtk.Functions.AcceleratorName(keyval, (Gdk.ModifierType)shortcut.GdkModifiers)));
        return menuItem;
    }

    private void OnMenuAction(Gtk4MenuModel model, PlannedMenuEntry entry)
    {
        try
        {
            if (!IsOpen) return;
            if (model.Context)
            {
                if (_contextMenu is { } session && ReferenceEquals(session.Model, model)) session.Chosen = entry.Item;
                return;
            }
            // After GTK has closed the popover, so a command that replaces the bar does not remove it during the click.
            if (entry is { Enabled: true, Item: { } item })
                Gtk4Dispatcher.Instance.Post(() =>
                {
                    if (IsOpen) RunMenuItem(item);
                });
        }
        catch
        {
            // Never unwind through GTK.
        }
    }

    // As AppKit validates items: enabled while the declaration and a target's CanExecute allow it.
    private void RefreshBarActions(Gtk4MenuModel model)
    {
        try
        {
            if (!ReferenceEquals(model, _barModel)) return;
            foreach (var (action, entry) in model.Items)
            {
                action.SetEnabled(entry.Enabled && entry.Item is { } item && MenuCommands is { } commands && commands.CanExecute(item));
            }
        }
        catch
        {
            // Never unwind through GTK.
        }
    }

    private bool RunMenuItem(DesktopMenuItem item)
    {
        // Runic Desktop's target logs and swallows a target's failure (event 3014).
        if (MenuCommands is not { } commands || !commands.CanExecute(item)) return false;
        commands.Execute(item);
        return true;
    }

    // The unhandled-key path: the bar's shortcut for a key the page left alone. True when an item ran.
    private bool TryRunShortcut(Gtk.EventControllerKey controller, Gtk.EventControllerKey.KeyPressedSignalArgs args)
    {
        if (_shortcuts.Count == 0) return false;
        Span<uint> alternates = stackalloc uint[8];
        var found = controller.GetWidget() is { } widget
            ? KeyLevels.Find(widget.GetDisplay().Handle.DangerousGetHandle(), args.Keycode, KeyLevels.AnyGroup, alternates)
            : 0;
        return _shortcuts.Match(args.Keyval, (uint)args.State, alternates[..found]) is { Enabled: true, Item: { } item } && RunMenuItem(item);
    }

    public ValueTask<DesktopMenuItem?> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsOpen) return ValueTask.FromException<DesktopMenuItem?>(new InvalidOperationException("The GTK 4 window is not open."));
        var session = new Gtk4ContextMenu();
        var shown = Gtk4Dispatcher.Instance.InvokeAsync(() => ShowContextMenu(session, items, options), cancellationToken);
        return new ValueTask<DesktopMenuItem?>(WaitForContextMenuAsync(session, shown, cancellationToken));
    }

    private async Task<DesktopMenuItem?> WaitForContextMenuAsync(Gtk4ContextMenu session, Task shown, CancellationToken cancellationToken)
    {
        await shown.ConfigureAwait(false);
        await using var registration = cancellationToken.Register(() =>
        {
            if (session.Result.TrySetCanceled(cancellationToken))
                _ = Gtk4Dispatcher.Instance.InvokeAsync(() => FinishContextMenu(session), CancellationToken.None);
        }).ConfigureAwait(false);
        return await session.Result.Task.ConfigureAwait(false);
    }

    private void ShowContextMenu(Gtk4ContextMenu session, ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options)
    {
        var webView = GetWebView();
        if (_contextMenu is { } open)
        {
            // A new context menu of the window dismisses the open one, which completes with null.
            open.Chosen = null;
            FinishContextMenu(open);
        }
        var model = new Gtk4MenuModel(ContextPrefix, new MenuIdAllocator(), context: true);
        var menu = BuildMenu(DesktopMenuPlanner.PlanEntries(items, MenuPlatform.Linux), model);
        webView.InsertActionGroup(ContextPrefix, model.Actions);
        var popover = Gtk.PopoverMenu.NewFromModel(menu);
        session.Model = model;
        session.Popover = popover;
        _contextMenu = session;
        popover.SetParent(webView);
        popover.SetHasArrow(false);
        popover.SetPosition(Gtk.PositionType.Bottom);
        if (Direction is { } direction) SetDirection(popover, direction);
        // The menu starts at the point and opens towards the end of the line.
        popover.SetHalign(popover.GetDirection() == Gtk.TextDirection.Rtl ? Gtk.Align.End : Gtk.Align.Start);
        var (x, y) = options is { X: { } pageX, Y: { } pageY }
            ? GtkMenus.PagePoint(pageX, pageY, webView.GetZoomLevel())
            : _pointer is { } pointer
                ? ((int)Math.Round(pointer.X), (int)Math.Round(pointer.Y))
                : (webView.GetWidth() / 2, webView.GetHeight() / 2);
        using var rectangle = new Gdk.Rectangle { X = x, Y = y, Width = 1, Height = 1 };
        popover.SetPointingTo(rectangle);
        popover.OnClosed += (_, _) =>
        {
            // GTK closes the popover around the chosen item's activation; finish from the next iteration.
            if (ReferenceEquals(_contextMenu, session)) Gtk4Dispatcher.Instance.Post(() => FinishContextMenu(session));
        };
        popover.Popup();
    }

    // GTK thread only. Completes the session with its chosen item and removes its popover; repeated calls do nothing.
    private void FinishContextMenu(Gtk4ContextMenu session)
    {
        if (ReferenceEquals(_contextMenu, session))
        {
            _contextMenu = null;
            _webView?.InsertActionGroup(ContextPrefix, null);
        }
        if (session.Popover is { } popover)
        {
            session.Popover = null;
            popover.Popdown();
            popover.Unparent();
            popover.Dispose();
        }
        session.Result.TrySetResult(session.Chosen);
    }

    // GTK thread, before the window's native objects are released: an open context menu completes with null.
    private void ReleaseMenus(WebKit.WebView webView)
    {
        StopGlobalMenu();
        if (_contextMenu is { } session)
        {
            session.Chosen = null;
            FinishContextMenu(session);
        }
        if (_pointerMotion is { } motion)
        {
            motion.OnMotion -= OnPointerMotion;
            webView.RemoveController(motion);
            motion.Dispose();
            _pointerMotion = null;
        }
        _window?.InsertActionGroup(BarPrefix, null);
        // GirCore's wrappers hold references: the box would keep the web view alive after the window closed.
        ReleaseWrappers(_barModel, _menuBar);
        _barModel = null;
        _menuBar = null;
        _menuBox?.Dispose();
        _menuBox = null;
        _shortcuts = GtkMenuShortcuts.Empty;
        _plan = [];
    }

    // B10, D036.9: the bar in the desktop's global menu (LinuxGlobalMenu). On X11, a window with the Native presentation
    // that is not a kiosk exports its bar once it is realized, and LinuxDesktopOptions.GlobalMenu allows it. While the
    // global menu shows it, the in-window bar is hidden, not removed; a frameless window has none.
    private void StartGlobalMenu(DesktopWindowHostOptions options, Gtk.Window window)
    {
        if (!GtkMenus.UsesGlobalMenu(options.MenuBarPresentation, options.Kiosk, options.GlobalMenu)
            || Gtk4X11Placement.X11Window(SurfaceOf(window)) is not { Xid: not 0 and <= uint.MaxValue } x11)
            return;
        _globalMenu = LinuxGlobalMenu.Start(new GlobalMenuWindow(this), (uint)x11.Xid, _plan);
    }

    // GTK thread: the bar went into the global menu or came back.
    private void OnGlobalPlacement(bool global)
    {
        if (!IsOpen) return;
        _globalPlacement = global;
        _menuBar?.SetVisible(!global);
        MenuBarPlacementChanged?.Invoke(this, EventArgs.Empty);
    }

    // GTK thread, when the window closes.
    private void StopGlobalMenu()
    {
        var menu = _globalMenu;
        _globalMenu = null;
        _globalPlacement = false;
        if (menu is not null) _ = menu.DisposeAsync().AsTask();
    }


    private sealed class GlobalMenuWindow(Gtk4WindowHost host) : ILinuxGlobalMenuWindow
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken) => Gtk4Dispatcher.Instance.InvokeAsync(() =>
        {
            if (host.IsOpen) action();
        }, cancellationToken);

        public bool CanExecute(DesktopMenuItem item) => host.MenuCommands is { } commands && commands.CanExecute(item);

        public void Execute(DesktopMenuItem item) => host.RunMenuItem(item);

        public void PlacementChanged(bool global) => host.OnGlobalPlacement(global);

        public void Annotate(string serviceName, string objectPath)
        {
            if (host._window is { } window && Gtk4X11Placement.X11Window(SurfaceOf(window)) is { Xid: not 0 } x11)
                LinuxGlobalMenu.X11.Annotate(x11.Display, x11.Xid, serviceName, objectPath);
        }

        public void Report(DesktopDiagnostic diagnostic) => host._options?.DiagnosticSink?.Invoke(diagnostic);
    }

    private static void ReleaseWrappers(Gtk4MenuModel? model, Gtk.PopoverMenuBar? bar)
    {
        if (model is not null)
        {
            foreach (var popover in model.Popovers) popover.Dispose();
            model.Popovers.Clear();
        }
        bar?.Dispose();
    }

    // A bar's or context menu's actions: each item's action with its entry, and the bar's popovers.
    private sealed class Gtk4MenuModel(string prefix, MenuIdAllocator ids, bool context)
    {
        internal string Prefix { get; } = prefix;
        internal MenuIdAllocator Ids { get; } = ids;
        internal bool Context { get; } = context;
        internal Gio.SimpleActionGroup Actions { get; } = Gio.SimpleActionGroup.New();
        internal List<(Gio.SimpleAction Action, PlannedMenuEntry Entry)> Items { get; } = [];
        internal List<Gtk.Popover> Popovers { get; } = [];
    }

    private sealed class Gtk4ContextMenu
    {
        internal TaskCompletionSource<DesktopMenuItem?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // GTK thread only.
        internal Gtk4MenuModel? Model { get; set; }
        internal Gtk.PopoverMenu? Popover { get; set; }
        internal DesktopMenuItem? Chosen { get; set; }
    }
}
