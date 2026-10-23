using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// B10: the GTK 3 window's menu bar, its shortcuts and its context menus.
// - The bar is a GtkMenuBar above the web view in a vertical GtkBox, for the Native presentation of a decorated
//   window. Items show their shortcut with gtk_accel_label_set_accel and no accel group, so a shortcut never runs
//   before the page has seen the key: the unhandled-key handler matches it (GtkMenuShortcuts).
// - Each submenu asks CanExecute when it is shown. Check and radio items show the declared state; a click runs the
//   command from the next main-loop iteration and puts the declared state back, which SetMenuBar then changes.
// - A context menu is a GtkMenu attached to the web view and popped up with the last button or key press on the view
//   as its trigger event, because a Wayland popup grab needs the serial of a recent input event.
internal sealed partial class LinuxWebKitGtkHost
{
    private static readonly Lazy<GtkMenuApi> MenuApiValue = new(static () => new());
    private static GtkMenuApi MenuApi => MenuApiValue.Value;

    private IDesktopMenuCommandTarget? _menuCommands;
    // GTK thread only: the box holding the bar and the view (0 when the window shows no bar in it), the bar, the
    // widgets of its items, the shortcuts of the declared bar, the open context menu and the last trigger event.
    private nint _menuBox;
    private nint _menuBar;
    private Gtk3MenuWidgets? _barWidgets;
    private GtkMenuShortcuts _shortcuts = GtkMenuShortcuts.Empty;
    // The planned bar, which the global menu exports too.
    private System.Collections.Immutable.ImmutableArray<PlannedMenuEntry> _plan = [];
    private bool _menuSignalsMuted;
    private Gtk3ContextMenu? _contextMenu;
    private nint _triggerEvent;

    public IDesktopMenuCommandTarget? MenuCommands
    {
        get => Volatile.Read(ref _menuCommands);
        set => Volatile.Write(ref _menuCommands, value);
    }

    public DesktopMenuBarPlacement MenuBarPlacement => IsOpen && _options is { } options
        ? GtkMenus.Placement(options.MenuBarPresentation, options.Frameless, options.Kiosk, _globalPlacement)
        : DesktopMenuBarPlacement.None;

    public ValueTask SetMenuBarAsync(IReadOnlyList<DesktopMenu> menus, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menus);
        return InvokeWindowAsync(() => ApplyMenuBar(menus), cancellationToken);
    }

    // Internal for the smokes: the bar's GtkMenuBar, or 0.
    internal nint MenuBarWidget => _menuBar;

    // Internal for the smokes: the open context menu's GtkMenu, or 0.
    internal nint ContextMenuWidget => _contextMenu?.Menu ?? 0;

    // Creates the box of a window that shows its bar in the window; the bar itself follows SetMenuBarAsync.
    private nint CreateMenuBox(WebUiEmbeddedHostOptions options)
    {
        if (!GtkMenus.ShowsInWindowBar(options.MenuBarPresentation, options.Frameless, options.Kiosk) || !MenuApi.IsAvailable) return 0;
        _menuBox = MenuApi.BoxNew(vertical: true);
        MenuApi.BoxPackStart(_menuBox, _webView, expand: true);
        return _menuBox;
    }

    private void ApplyMenuBar(IReadOnlyList<DesktopMenu> menus)
    {
        var plan = DesktopMenuPlanner.PlanBar(menus, MenuPlatform.Linux);
        _shortcuts = GtkMenuShortcuts.Create(plan);
        _plan = plan;
        _globalMenu?.SetMenus(plan);
        if (_menuBar != 0)
        {
            Api.GtkWidgetDestroy(_menuBar);
            _menuBar = 0;
        }
        _barWidgets = null;
        if (_menuBox == 0 || plan.IsEmpty) return;
        var widgets = new Gtk3MenuWidgets(context: false, LayoutDirection);
        var bar = MenuApi.MenuBarNew();
        widgets.SetDirection(bar);
        AddMenuEntries(bar, plan, widgets);
        MenuApi.BoxPackStart(_menuBox, bar, expand: false);
        MenuApi.BoxReorderChild(_menuBox, bar, 0);
        Api.GtkWidgetShowAll(bar);
        // The global menu shows the bar: the window keeps it hidden until the global menu goes away.
        if (_globalPlacement) Api.GtkWidgetHide(bar);
        _menuBar = bar;
        _barWidgets = widgets;
    }

    // GtkTextDirection for the window's menus: 0 when the window keeps GTK's default.
    private int LayoutDirection => _options?.LayoutDirection switch
    {
        Runic.Platform.DesktopLayoutDirection.RightToLeft => 2,
        Runic.Platform.DesktopLayoutDirection.LeftToRight => 1,
        _ => 0,
    };

    private unsafe void AddMenuEntries(nint shell, ImmutableArray<PlannedMenuEntry> entries, Gtk3MenuWidgets widgets)
    {
        var radioGroups = GtkMenus.SingleCheckedRadioGroups(entries);
        var firstOfGroup = new Dictionary<string, nint>(StringComparer.Ordinal);
        var leaves = new List<(nint Widget, PlannedMenuEntry Entry)>();
        _menuSignalsMuted = true;
        try
        {
            foreach (var entry in entries)
            {
                nint widget;
                switch (entry.Kind)
                {
                    case PlannedMenuKind.Separator:
                        widget = MenuApi.SeparatorMenuItemNew();
                        break;
                    case PlannedMenuKind.Menu:
                        widget = MenuApi.MenuItemNew(entry.MnemonicLabel);
                        var submenu = MenuApi.MenuNew();
                        widgets.SetDirection(submenu);
                        AddMenuEntries(submenu, entry.Children, widgets);
                        MenuApi.MenuItemSetSubmenu(widget, submenu);
                        if (!widgets.Context)
                            Api.Connect(submenu, "show", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnBarMenuShown, this);
                        break;
                    case PlannedMenuKind.Check:
                        widget = MenuApi.CheckMenuItemNew(entry.MnemonicLabel);
                        MenuApi.CheckMenuItemSetActive(widget, entry.Item?.IsChecked == true);
                        widgets.Toggles.Add((widget, entry, false));
                        break;
                    case PlannedMenuKind.Radio when entry.Item is { RadioGroup: { } group } && radioGroups.Contains(group):
                        var first = firstOfGroup.GetValueOrDefault(group);
                        widget = MenuApi.RadioMenuItemNew(first, entry.MnemonicLabel);
                        if (first == 0) firstOfGroup[group] = widget;
                        if (entry.Item.IsChecked == true) MenuApi.CheckMenuItemSetActive(widget, true);
                        widgets.Toggles.Add((widget, entry, true));
                        widgets.Radios.Add(widget);
                        break;
                    case PlannedMenuKind.Radio:
                        // GTK radio items keep one item checked; a group with none or several checked is drawn as radio
                        // items with the declared state.
                        widget = MenuApi.CheckMenuItemNew(entry.MnemonicLabel);
                        MenuApi.CheckMenuItemSetDrawAsRadio(widget, true);
                        MenuApi.CheckMenuItemSetActive(widget, entry.Item?.IsChecked == true);
                        widgets.Toggles.Add((widget, entry, false));
                        break;
                    default:
                        widget = MenuApi.MenuItemNew(entry.MnemonicLabel);
                        break;
                }
                if (entry is { Kind: not (PlannedMenuKind.Menu or PlannedMenuKind.Separator), Shortcut: { GdkKeyval: { } keyval } shortcut })
                    MenuApi.ShowAccelerator(widget, keyval, shortcut.GdkModifiers);
                MenuApi.WidgetSetSensitive(widget, entry.Enabled);
                widgets.SetDirection(widget);
                if (entry.Item is not null)
                {
                    widgets.Items[widget] = entry;
                    leaves.Add((widget, entry));
                    Api.Connect(widget, "activate", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnMenuItemActivated, this);
                }
                MenuApi.MenuShellAppend(shell, widget);
            }
        }
        finally
        {
            _menuSignalsMuted = false;
        }
        widgets.Menus[shell] = leaves;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBarMenuShown(nint menu, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { IsOpen: true, _barWidgets: { } widgets } host
                && widgets.Menus.TryGetValue(menu, out var leaves))
            {
                // As AppKit validates items: enabled while the declaration and a target's CanExecute allow it.
                foreach (var (widget, entry) in leaves)
                {
                    MenuApi.WidgetSetSensitive(widget, entry.Enabled && entry.Item is { } item && host.CanExecuteMenuItem(item));
                }
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMenuItemActivated(nint widget, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not LinuxWebKitGtkHost { IsOpen: true, _menuSignalsMuted: false } host) return;
            // Choosing a GTK radio item also activates the item of its group that it unchecks; only the chosen one counts.
            if (IsUncheckedRadio(host._contextMenu?.Widgets, widget) || IsUncheckedRadio(host._barWidgets, widget)) return;
            if (host._contextMenu is { } session && session.Widgets.Items.TryGetValue(widget, out var chosen))
            {
                session.Chosen = chosen.Item;
                return;
            }
            if (host._barWidgets is { } widgets && widgets.Items.TryGetValue(widget, out var entry) && entry.Item is { } item)
            {
                // After GTK has closed the menu, so a command that replaces the bar does not destroy the item while
                // it is being activated.
                Dispatcher.Post(() =>
                {
                    if (!host.IsOpen) return;
                    host.RestoreToggles(widgets);
                    if (entry.Enabled) host.RunMenuItem(item);
                });
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    private static bool IsUncheckedRadio(Gtk3MenuWidgets? widgets, nint widget) =>
        widgets is not null && widgets.Radios.Contains(widget) && !MenuApi.CheckMenuItemGetActive(widget);

    // A click toggled the item; the declaration decides what the bar shows.
    private void RestoreToggles(Gtk3MenuWidgets widgets)
    {
        if (!ReferenceEquals(widgets, _barWidgets)) return;
        _menuSignalsMuted = true;
        try
        {
            foreach (var (widget, entry, radio) in widgets.Toggles)
            {
                var declared = entry.Item?.IsChecked == true;
                if (radio)
                {
                    // Checking the declared item unchecks the rest of its GTK group.
                    if (declared && !MenuApi.CheckMenuItemGetActive(widget)) MenuApi.CheckMenuItemSetActive(widget, true);
                }
                else if (MenuApi.CheckMenuItemGetActive(widget) != declared)
                {
                    MenuApi.CheckMenuItemSetActive(widget, declared);
                }
            }
        }
        finally
        {
            _menuSignalsMuted = false;
        }
    }

    private bool CanExecuteMenuItem(DesktopMenuItem item) => MenuCommands is { } commands && commands.CanExecute(item);

    private bool RunMenuItem(DesktopMenuItem item) => MenuCommands switch
    {
        MenuCommandChain chain => chain.TryExecute(item),
        { } commands when commands.CanExecute(item) => Execute(commands, item),
        _ => false,
    };

    private static bool Execute(IDesktopMenuCommandTarget commands, DesktopMenuItem item)
    {
        commands.Execute(item);
        return true;
    }

    // The unhandled-key path: the bar's shortcut for a key the page left alone. True when an item ran.
    private bool TryRunShortcut(uint keyval, uint state, ReadOnlySpan<uint> alternates) =>
        _shortcuts.Match(keyval, state, alternates) is { Enabled: true, Item: { } item } && RunMenuItem(item);

    public ValueTask<DesktopMenuItem?> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!MenuApi.IsAvailable) return ValueTask.FromException<DesktopMenuItem?>(new NotSupportedException("This GTK 3 shows no context menus."));
        cancellationToken.ThrowIfCancellationRequested();
        var session = new Gtk3ContextMenu();
        var shown = InvokeWindowAsync(() => ShowContextMenu(session, items, options), cancellationToken);
        return new ValueTask<DesktopMenuItem?>(WaitForContextMenuAsync(session, shown, cancellationToken));
    }

    private async Task<DesktopMenuItem?> WaitForContextMenuAsync(Gtk3ContextMenu session, ValueTask shown, CancellationToken cancellationToken)
    {
        await shown.ConfigureAwait(false);
        await using var registration = cancellationToken.Register(() =>
        {
            if (session.Result.TrySetCanceled(cancellationToken))
                _ = Dispatcher.InvokeAsync(() => FinishContextMenu(session), CancellationToken.None);
        }).ConfigureAwait(false);
        return await session.Result.Task.ConfigureAwait(false);
    }

    private unsafe void ShowContextMenu(Gtk3ContextMenu session, ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options)
    {
        if (_contextMenu is { } open)
        {
            // A new context menu of the window dismisses the open one, which completes with null.
            open.Chosen = null;
            FinishContextMenu(open);
        }
        var menu = MenuApi.MenuNew();
        session.Menu = menu;
        session.Widgets = new Gtk3MenuWidgets(context: true, LayoutDirection);
        session.Widgets.SetDirection(menu);
        AddMenuEntries(menu, DesktopMenuPlanner.PlanEntries(items, MenuPlatform.Linux), session.Widgets);
        _contextMenu = session;
        MenuApi.MenuAttachToWidget(menu, _webView);
        Api.Connect(menu, "deactivate", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnContextMenuDeactivated, this);
        Api.GtkWidgetShowAll(menu);
        if (options is { X: { } x, Y: { } y })
        {
            var (pointX, pointY) = GtkMenus.PagePoint(x, y, Api.GetZoomLevel(_webView));
            MenuApi.MenuPopupAtPoint(menu, _webView, pointX, pointY, _triggerEvent);
        }
        else
        {
            MenuApi.MenuPopupAtPointer(menu, _triggerEvent);
        }
        // GTK shows nothing when it cannot grab the pointer and keyboard; the menu is then dismissed.
        if (!MenuApi.WidgetGetVisible(menu)) FinishContextMenu(session);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnContextMenuDeactivated(nint menu, nint context)
    {
        try
        {
            // GTK deactivates the menu before it activates the chosen item, so finish from the next iteration.
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost host && host._contextMenu is { } session && session.Menu == menu)
                Dispatcher.Post(() => host.FinishContextMenu(session));
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    // GTK thread only. Completes the session with its chosen item and destroys its menu; repeated calls do nothing.
    private void FinishContextMenu(Gtk3ContextMenu session)
    {
        if (ReferenceEquals(_contextMenu, session)) _contextMenu = null;
        var menu = session.Menu;
        session.Menu = 0;
        if (menu != 0) Api.GtkWidgetDestroy(menu);
        session.Result.TrySetResult(session.Chosen);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnTriggerEvent(nint widget, nint nativeEvent, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is LinuxWebKitGtkHost { IsOpen: true } host && nativeEvent != 0)
            {
                var copy = MenuApi.EventCopy(nativeEvent);
                if (host._triggerEvent != 0) MenuApi.EventFree(host._triggerEvent);
                host._triggerEvent = copy;
                // A primary button press is also the trigger event of a drag out (B11).
                host.KeepDragPress(nativeEvent);
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
        // WebKit and the page still get the event.
        return 0;
    }

    private unsafe void ConnectMenuEvents()
    {
        if (!MenuApi.IsAvailable) return;
        Api.Connect(_webView, "button-press-event", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnTriggerEvent, this);
        Api.Connect(_webView, "key-press-event", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnTriggerEvent, this);
    }

    // GTK thread, when the window is destroyed: an open context menu completes with null.
    private void ReleaseMenus()
    {
        StopGlobalMenu();
        if (_contextMenu is { } session)
        {
            session.Chosen = null;
            FinishContextMenu(session);
        }
        if (_triggerEvent != 0)
        {
            MenuApi.EventFree(_triggerEvent);
            _triggerEvent = 0;
        }
        _barWidgets = null;
        _shortcuts = GtkMenuShortcuts.Empty;
        _plan = [];
        _menuBar = 0;
        _menuBox = 0;
    }

    // The widgets of a bar or context menu, GTK thread only: leaf items with their entries, each menu's leaf items for
    // the CanExecute refresh, and the toggle items with whether GTK groups them as radio items.
    private sealed class Gtk3MenuWidgets(bool context, int direction)
    {
        internal bool Context { get; } = context;
        internal Dictionary<nint, PlannedMenuEntry> Items { get; } = [];
        internal Dictionary<nint, List<(nint Widget, PlannedMenuEntry Entry)>> Menus { get; } = [];
        internal List<(nint Widget, PlannedMenuEntry Entry, bool Radio)> Toggles { get; } = [];
        internal HashSet<nint> Radios { get; } = [];

        internal void SetDirection(nint widget)
        {
            if (direction != 0) Api.GtkWidgetSetDirection(widget, direction);
        }
    }

    private sealed class Gtk3ContextMenu
    {
        internal TaskCompletionSource<DesktopMenuItem?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // GTK thread only.
        internal nint Menu { get; set; }
        internal Gtk3MenuWidgets Widgets { get; set; } = new(context: true, 0);
        internal DesktopMenuItem? Chosen { get; set; }
    }

    // The GTK 3 menu functions; GTK 3.22, which WebKitGTK 4.1 requires, has every one.
    private sealed unsafe class GtkMenuApi
    {
        private readonly nint _boxNew, _boxPackStart, _boxReorderChild, _menuBarNew, _menuNew, _menuItemNewWithMnemonic,
            _checkMenuItemNewWithMnemonic, _radioMenuItemNewWithMnemonicFromWidget, _separatorMenuItemNew, _menuItemSetSubmenu,
            _menuShellAppend, _checkMenuItemSetActive, _checkMenuItemGetActive, _checkMenuItemSetDrawAsRadio, _widgetSetSensitive,
            _binGetChild, _accelLabelSetAccel, _menuAttachToWidget, _menuPopupAtPointer, _menuPopupAtRect, _widgetGetWindow,
            _widgetGetHasWindow, _widgetGetAllocation, _widgetGetVisible, _eventCopy, _eventFree;

        internal GtkMenuApi()
        {
            if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libgtk-3.so.0", out var gtk)) return;
            nint Export(string name) => NativeLibrary.TryGetExport(gtk, name, out var address) ? address : 0;
            _boxNew = Export("gtk_box_new");
            _boxPackStart = Export("gtk_box_pack_start");
            _boxReorderChild = Export("gtk_box_reorder_child");
            _menuBarNew = Export("gtk_menu_bar_new");
            _menuNew = Export("gtk_menu_new");
            _menuItemNewWithMnemonic = Export("gtk_menu_item_new_with_mnemonic");
            _checkMenuItemNewWithMnemonic = Export("gtk_check_menu_item_new_with_mnemonic");
            _radioMenuItemNewWithMnemonicFromWidget = Export("gtk_radio_menu_item_new_with_mnemonic_from_widget");
            _separatorMenuItemNew = Export("gtk_separator_menu_item_new");
            _menuItemSetSubmenu = Export("gtk_menu_item_set_submenu");
            _menuShellAppend = Export("gtk_menu_shell_append");
            _checkMenuItemSetActive = Export("gtk_check_menu_item_set_active");
            _checkMenuItemGetActive = Export("gtk_check_menu_item_get_active");
            _checkMenuItemSetDrawAsRadio = Export("gtk_check_menu_item_set_draw_as_radio");
            _widgetSetSensitive = Export("gtk_widget_set_sensitive");
            _binGetChild = Export("gtk_bin_get_child");
            _accelLabelSetAccel = Export("gtk_accel_label_set_accel");
            _menuAttachToWidget = Export("gtk_menu_attach_to_widget");
            _menuPopupAtPointer = Export("gtk_menu_popup_at_pointer");
            _menuPopupAtRect = Export("gtk_menu_popup_at_rect");
            _widgetGetWindow = Export("gtk_widget_get_window");
            _widgetGetHasWindow = Export("gtk_widget_get_has_window");
            _widgetGetAllocation = Export("gtk_widget_get_allocation");
            _widgetGetVisible = Export("gtk_widget_get_visible");
            _eventCopy = Export("gdk_event_copy");
            _eventFree = Export("gdk_event_free");
            IsAvailable = new[]
            {
                _boxNew, _boxPackStart, _boxReorderChild, _menuBarNew, _menuNew, _menuItemNewWithMnemonic, _checkMenuItemNewWithMnemonic,
                _radioMenuItemNewWithMnemonicFromWidget, _separatorMenuItemNew, _menuItemSetSubmenu, _menuShellAppend,
                _checkMenuItemSetActive, _checkMenuItemGetActive, _checkMenuItemSetDrawAsRadio, _widgetSetSensitive, _binGetChild,
                _accelLabelSetAccel, _menuAttachToWidget, _menuPopupAtPointer, _menuPopupAtRect, _widgetGetWindow,
                _widgetGetHasWindow, _widgetGetAllocation, _widgetGetVisible, _eventCopy, _eventFree,
            }.All(static address => address != 0);
        }

        internal bool IsAvailable { get; }

        internal nint BoxNew(bool vertical) => ((delegate* unmanaged[Cdecl]<int, int, nint>)_boxNew)(vertical ? 1 : 0, 0);

        internal void BoxPackStart(nint box, nint child, bool expand) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, int, int, uint, void>)_boxPackStart)(box, child, expand ? 1 : 0, expand ? 1 : 0, 0);

        internal void BoxReorderChild(nint box, nint child, int position) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, int, void>)_boxReorderChild)(box, child, position);

        internal nint MenuBarNew() => ((delegate* unmanaged[Cdecl]<nint>)_menuBarNew)();

        internal nint MenuNew() => ((delegate* unmanaged[Cdecl]<nint>)_menuNew)();

        internal nint MenuItemNew(string label) => WithLabel(_menuItemNewWithMnemonic, label);

        internal nint CheckMenuItemNew(string label) => WithLabel(_checkMenuItemNewWithMnemonic, label);

        internal nint RadioMenuItemNew(nint groupMember, string label)
        {
            var text = Marshal.StringToCoTaskMemUTF8(label);
            try
            {
                return ((delegate* unmanaged[Cdecl]<nint, nint, nint>)_radioMenuItemNewWithMnemonicFromWidget)(groupMember, text);
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
            }
        }

        private static nint WithLabel(nint function, string label)
        {
            var text = Marshal.StringToCoTaskMemUTF8(label);
            try
            {
                return ((delegate* unmanaged[Cdecl]<nint, nint>)function)(text);
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
            }
        }

        internal nint SeparatorMenuItemNew() => ((delegate* unmanaged[Cdecl]<nint>)_separatorMenuItemNew)();

        internal void MenuItemSetSubmenu(nint item, nint submenu) => ((delegate* unmanaged[Cdecl]<nint, nint, void>)_menuItemSetSubmenu)(item, submenu);

        internal void MenuShellAppend(nint shell, nint item) => ((delegate* unmanaged[Cdecl]<nint, nint, void>)_menuShellAppend)(shell, item);

        internal void CheckMenuItemSetActive(nint item, bool active) =>
            ((delegate* unmanaged[Cdecl]<nint, int, void>)_checkMenuItemSetActive)(item, active ? 1 : 0);

        internal bool CheckMenuItemGetActive(nint item) => ((delegate* unmanaged[Cdecl]<nint, int>)_checkMenuItemGetActive)(item) != 0;

        internal void CheckMenuItemSetDrawAsRadio(nint item, bool radio) =>
            ((delegate* unmanaged[Cdecl]<nint, int, void>)_checkMenuItemSetDrawAsRadio)(item, radio ? 1 : 0);

        internal void WidgetSetSensitive(nint widget, bool sensitive) =>
            ((delegate* unmanaged[Cdecl]<nint, int, void>)_widgetSetSensitive)(widget, sensitive ? 1 : 0);

        internal bool WidgetGetVisible(nint widget) => ((delegate* unmanaged[Cdecl]<nint, int>)_widgetGetVisible)(widget) != 0;

        // The item's label is a GtkAccelLabel; setting its accelerator shows the text without registering the key.
        internal void ShowAccelerator(nint item, uint keyval, uint modifiers)
        {
            var label = ((delegate* unmanaged[Cdecl]<nint, nint>)_binGetChild)(item);
            if (label != 0) ((delegate* unmanaged[Cdecl]<nint, uint, uint, void>)_accelLabelSetAccel)(label, keyval, modifiers);
        }

        internal void MenuAttachToWidget(nint menu, nint widget) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)_menuAttachToWidget)(menu, widget, 0);

        internal void MenuPopupAtPointer(nint menu, nint triggerEvent) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, void>)_menuPopupAtPointer)(menu, triggerEvent);

        // At a point of the widget: its top-left corner (top-right in a right-to-left menu, which GTK flips).
        internal void MenuPopupAtPoint(nint menu, nint widget, int x, int y, nint triggerEvent)
        {
            var window = ((delegate* unmanaged[Cdecl]<nint, nint>)_widgetGetWindow)(widget);
            if (window == 0)
            {
                MenuPopupAtPointer(menu, triggerEvent);
                return;
            }
            if (((delegate* unmanaged[Cdecl]<nint, int>)_widgetGetHasWindow)(widget) == 0)
            {
                // A widget without its own GdkWindow is placed in its parent's.
                GdkRectangle allocation;
                ((delegate* unmanaged[Cdecl]<nint, GdkRectangle*, void>)_widgetGetAllocation)(widget, &allocation);
                x += allocation.X;
                y += allocation.Y;
            }
            var rectangle = new GdkRectangle { X = x, Y = y, Width = 1, Height = 1 };
            const int NorthWest = 1;
            ((delegate* unmanaged[Cdecl]<nint, nint, GdkRectangle*, int, int, nint, void>)_menuPopupAtRect)(
                menu, window, &rectangle, NorthWest, NorthWest, triggerEvent);
        }

        internal nint EventCopy(nint nativeEvent) => ((delegate* unmanaged[Cdecl]<nint, nint>)_eventCopy)(nativeEvent);

        internal void EventFree(nint nativeEvent) => ((delegate* unmanaged[Cdecl]<nint, void>)_eventFree)(nativeEvent);

        [StructLayout(LayoutKind.Sequential)]
        private struct GdkRectangle
        {
            internal int X;
            internal int Y;
            internal int Width;
            internal int Height;
        }
    }
}
