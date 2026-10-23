using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// The window's menu bar, its shortcuts and context menus (B10). Window thread only, except where noted.
internal sealed partial class WindowsWebView2Host
{
    private const uint WmCancelMode = 0x001F;
    private const uint WmCommand = 0x0111;
    private const uint WmSysCommand = 0x0112;
    private const uint WmInitMenuPopup = 0x0117;
    private const uint WmEnterIdle = 0x0121;
    private const uint WmEnterMenuLoop = 0x0211;
    private const uint WmExitMenuLoop = 0x0212;
    private const nuint ScKeyMenu = 0xF100;
    private const nuint MsgfMenu = 2;
    private const uint MfByCommand = 0x0, MfGrayed = 0x1;
    private const uint TpmRightButton = 0x0002, TpmRightAlign = 0x0008, TpmReturnCommand = 0x0100, TpmLayoutRtl = 0x8000;
    private const int SmMenuDropAlignment = 40;

    private IDesktopMenuCommandTarget? _menuCommands;
    // The declared bar, planned for this window: shortcuts in every presentation, and the native bar for Native.
    // Item ids stay with their planned keys for the window's lifetime (MenuIdAllocator).
    private readonly MenuIdAllocator _menuIds = new(1, WindowsMenuModel.LastId);
    private WindowsMenuModel _menuBar = WindowsMenuModel.Empty;
    // The items of each popup of the native bar, for WM_INITMENUPOPUP.
    private Dictionary<nint, ImmutableArray<Win32MenuEntry>> _barPopups = [];
    // Menus that arrived while a menu was open: applied when it closes, so an open menu is never destroyed.
    private IReadOnlyList<DesktopMenu>? _pendingMenuBar;
    private bool _inMenuLoop;
    // The bar of a full-screen window, which shows none; it returns when the window leaves full screen.
    private nint _hiddenBar;
    private bool _destroyAfterMenu;
    private ContextMenuSession? _contextMenu;
    private (ContextMenuSession Session, ImmutableArray<DesktopMenuEntry> Items, DesktopContextMenuOptions Options)? _nextContextMenu;

    // Called on the window thread when a context menu's modal loop is idle, with the menu window: the native smoke
    // drives the menu with keys from here, as a user would.
    internal static Action<nint>? ContextMenuIdle { get; set; }

    public IDesktopMenuCommandTarget? MenuCommands
    {
        get => Volatile.Read(ref _menuCommands);
        set => Volatile.Write(ref _menuCommands, value);
    }

    // Frameless and kiosk windows have no frame to hold a bar.
    private bool ShowsNativeBar => _options is { MenuBarPresentation: DesktopMenuBarPresentation.Native, Frameless: false, Kiosk: false };

    private bool IsRightToLeft => _options?.LayoutDirection == DesktopLayoutDirection.RightToLeft;

    // The Page placement belongs to the in-page bar of a later release; until then a Page window reports None.
    public DesktopMenuBarPlacement MenuBarPlacement => IsOpen && ShowsNativeBar ? DesktopMenuBarPlacement.Window : DesktopMenuBarPlacement.None;

    // The placement never changes while the window is open.
    public event EventHandler? MenuBarPlacementChanged
    {
        add { }
        remove { }
    }

    public ValueTask SetMenuBarAsync(IReadOnlyList<DesktopMenu> menus, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menus);
        return InvokeAsync(() => ApplyMenuBar(menus), cancellationToken);
    }

    private void ApplyMenuBar(IReadOnlyList<DesktopMenu> menus)
    {
        if (_window == 0) return;
        if (_inMenuLoop)
        {
            _pendingMenuBar = menus;
            return;
        }
        _pendingMenuBar = null;
        var model = WindowsMenuModel.ForBar(menus, _menuIds, IsRightToLeft);
        _menuBar = model;
        if (!ShowsNativeBar) return;
        Dictionary<nint, ImmutableArray<Win32MenuEntry>> popups = [];
        var bar = model.Entries.IsEmpty ? 0 : CreateMenu(model.Entries, popup: false, popups);
        if (_fullScreen)
        {
            if (_hiddenBar != 0) Native.DestroyMenu(_hiddenBar);
            _hiddenBar = bar;
            _barPopups = popups;
            return;
        }
        var previous = Native.GetMenu(_window);
        if (!Native.SetMenu(_window, bar))
        {
            if (bar != 0) Native.DestroyMenu(bar);
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "The window's menu bar could not be set.");
        }
        _barPopups = popups;
        if (previous != 0) Native.DestroyMenu(previous);
        Native.DrawMenuBar(_window);
    }

    // DestroyMenu on the returned menu destroys its submenus.
    private static unsafe nint CreateMenu(ImmutableArray<Win32MenuEntry> entries, bool popup, Dictionary<nint, ImmutableArray<Win32MenuEntry>>? popups)
    {
        var menu = popup ? Native.CreatePopupMenu() : Native.CreateMenu();
        if (menu == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "A menu could not be created.");
        try
        {
            uint position = 0;
            foreach (var entry in entries)
            {
                var info = new MenuItemInfo
                {
                    Size = (uint)sizeof(MenuItemInfo),
                    Mask = MiimFType | MiimState | MiimId | MiimString,
                    Type = entry.Type,
                    State = entry.State,
                    Id = entry.Id,
                };
                if (entry.IsSubmenu)
                {
                    info.SubMenu = CreateMenu(entry.Children, popup: true, popups);
                    info.Mask |= MiimSubMenu;
                    popups?.Add(info.SubMenu, entry.Children);
                }
                bool inserted;
                fixed (char* label = entry.Label)
                {
                    info.TypeData = (nint)label;
                    info.Count = (uint)entry.Label.Length;
                    inserted = Native.InsertMenuItem(menu, position, true, in info);
                }
                if (!inserted)
                {
                    if (info.SubMenu != 0) Native.DestroyMenu(info.SubMenu);
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "A menu item could not be added.");
                }
                position++;
            }
            return menu;
        }
        catch
        {
            Native.DestroyMenu(menu);
            throw;
        }
    }

    // WM_INITMENUPOPUP: a popup of the bar is about to open, so its items follow their commands, as on macOS.
    private void InitializePopup(nint popup)
    {
        if (!_barPopups.TryGetValue(popup, out var entries)) return;
        var commands = MenuCommands;
        foreach (var entry in entries)
        {
            if (entry.Item is not { } item) continue;
            var enabled = entry.Planned.Enabled && commands is not null && commands.CanExecute(item);
            _ = Native.EnableMenuItem(popup, entry.Id, MfByCommand | (enabled ? 0 : MfGrayed));
        }
    }

    // WM_COMMAND from the bar: the item of the id, through the command path. Ids are never reused, so a choice that
    // arrives after SetMenuBar removed the item runs nothing.
    private void RunBarItem(uint id)
    {
        if (_menuBar.Items.TryGetValue(id, out var entry) && entry is { Planned.Enabled: true, Item: { } item })
            RunItem(item);
    }

    private void RunItem(DesktopMenuItem item)
    {
        switch (MenuCommands)
        {
            case MenuCommandChain chain:
                chain.TryExecute(item);
                break;
            case { } target when target.CanExecute(item):
                target.Execute(item);
                break;
        }
    }

    // From the WebView2 controller: whether the browser should leave a key to the menu, keeping its own accelerator off.
    private bool IsMenuShortcut(int virtualKey) => _menuBar.IsShortcut(virtualKey,
        Native.GetKeyState(VkControl) < 0, Native.GetKeyState(VkShift) < 0, Native.GetKeyState(VkMenu) < 0);

    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;

    // A key the page left unhandled. A declared shortcut runs its item in every presentation; Alt and a top-level
    // menu's access key opens that menu of the native bar, which WebView2 does not forward (an unhandled Alt or F10
    // alone reaches the window as SC_KEYMENU).
    private void OnMenuKey(MenuKeyReport report)
    {
        if (_window == 0) return;
        var model = _menuBar;
        if (model.Match(report.KeyCode, report.Key, report.Control, report.Shift, report.Alt, report.Meta) is { } entry)
        {
            if (entry is { Planned.Enabled: true, Item: { } item }) RunItem(item);
            return;
        }
        if (report is { Alt: true, Control: false, Meta: false } && ShowsNativeBar && Native.GetMenu(_window) != 0 && model.HasMenuAccessKey(report.Key))
            Native.PostMessage(_window, WmSysCommand, ScKeyMenu, char.ToLowerInvariant(report.Key[0]));
    }

    // WM_SYSCOMMAND SC_KEYMENU from the keyboard (lParam 0: Alt or F10 alone). Without a bar in the window it would put
    // the window into system-menu mode, which a page with its own bar or none does not expect.
    private bool SwallowsKeyMenu(nuint command, nint lParam) =>
        (command & 0xFFF0) == ScKeyMenu && lParam == 0 && (!ShowsNativeBar || _hiddenBar != 0);

    // Full screen shows no bar; shortcuts keep working.
    private void HideBarForFullScreen()
    {
        if (_hiddenBar != 0 || Native.GetMenu(_window) is not (not 0 and var bar)) return;
        if (_inMenuLoop) CancelMenuMode();
        if (Native.SetMenu(_window, 0)) _hiddenBar = bar;
    }

    private void ShowBarAfterFullScreen()
    {
        if (_hiddenBar == 0) return;
        if (Native.SetMenu(_window, _hiddenBar)) _hiddenBar = 0;
    }

    // WM_DESTROY destroys the window's bar; a hidden one is not the window's.
    private void DestroyHiddenBar()
    {
        if (_hiddenBar == 0) return;
        Native.DestroyMenu(_hiddenBar);
        _hiddenBar = 0;
    }

    private void EnterMenuLoop() => _inMenuLoop = true;

    private void ExitMenuLoop()
    {
        _inMenuLoop = false;
        if (_destroyAfterMenu)
        {
            // After the menu's loop returns.
            PostNative(() => { if (_window != 0) Native.DestroyWindow(_window); });
            return;
        }
        if (_pendingMenuBar is { } menus)
        {
            PostNative(() => { if (!_inMenuLoop && ReferenceEquals(_pendingMenuBar, menus)) ApplyMenuBar(menus); });
        }
        // A context menu asked for while the bar's menu was open; one that replaces a context menu opens after it.
        if (_contextMenu is null && _nextContextMenu is not null)
        {
            PostNative(ShowNextContextMenu);
        }
    }

    // WM_DESTROY: a context menu still waiting to open returns null.
    private void CompleteContextMenus()
    {
        _contextMenu?.Complete(null);
        if (_nextContextMenu is { } next)
        {
            _nextContextMenu = null;
            next.Session.Complete(null);
        }
    }

    // Closing the window while a menu is open closes the menu first; the window goes once its loop returned.
    private bool DeferDestroyForMenu()
    {
        if (!_inMenuLoop) return false;
        _destroyAfterMenu = true;
        _contextMenu?.Dismiss();
        CancelMenuMode();
        return true;
    }

    private sealed class ContextMenuSession(CancellationToken cancellationToken)
    {
        private int _dismissed;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal TaskCompletionSource<DesktopMenuItem?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsDismissed => Volatile.Read(ref _dismissed) != 0 || CancellationToken.IsCancellationRequested;
        internal void Dismiss() => Volatile.Write(ref _dismissed, 1);

        internal void Complete(DesktopMenuItem? chosen)
        {
            if (chosen is null && CancellationToken.IsCancellationRequested) Result.TrySetCanceled(CancellationToken);
            else Result.TrySetResult(chosen);
        }
    }

    public ValueTask<DesktopMenuItem?> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<DesktopMenuItem?>(ShowContextMenuCoreAsync(new ContextMenuSession(cancellationToken), items, options));
    }

    private async Task<DesktopMenuItem?> ShowContextMenuCoreAsync(ContextMenuSession session, ImmutableArray<DesktopMenuEntry> items,
        DesktopContextMenuOptions options)
    {
        // A new menu dismisses the open one, which returns null: WM_CANCELMODE reaches the open menu's loop.
        if (Volatile.Read(ref _contextMenu) is { } open)
        {
            open.Dismiss();
            CancelMenuMode();
        }
        // Cancelling closes the open menu. The work below returns once the menu has closed.
        using var registration = session.CancellationToken.UnsafeRegister(static state =>
        {
            var (host, session) = ((WindowsWebView2Host, ContextMenuSession))state!;
            host.DismissContextMenu(session);
        }, (this, session));
        try
        {
            await InvokeAsync(() => ShowContextMenu(session, items, options), session.CancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (!IsOpen)
        {
            // The window closed before its menu opened.
            return null;
        }
        return await session.Result.Task.ConfigureAwait(false);
    }

    // Any thread.
    private void DismissContextMenu(ContextMenuSession session)
    {
        session.Dismiss();
        if (ReferenceEquals(Volatile.Read(ref _contextMenu), session)) CancelMenuMode();
        PostNative(() =>
        {
            if (ReferenceEquals(_contextMenu, session)) CancelMenuMode();
            else if (_nextContextMenu is { } next && ReferenceEquals(next.Session, session)) { _nextContextMenu = null; session.Complete(null); }
        });
    }

    // Any thread: ends the window's open menu, a context menu or the bar's, as a click elsewhere would. The menu's
    // modal loop handles WM_CANCELMODE for its owner.
    private void CancelMenuMode()
    {
        var window = _window;
        if (window != 0) Native.PostMessage(window, WmCancelMode, 0, 0);
    }

    // Runs work on the window thread after the current message, without a caller to wait; nothing runs once the window closed.
    private void PostNative(Action action)
    {
        var window = _window;
        if (window == 0) return;
        _nativeWork.Enqueue(new NativeDispatchWork(action, CancellationToken.None));
        if (!Native.PostMessage(window, WmAppDispatch, 0, 0) || !IsOpen) FailNativeWork();
    }

    // A new context menu dismisses the open one, which returns null. Win32 tracks one menu per thread, and this work
    // can run inside an open menu's loop, so the new menu opens once that loop has returned: after TrackContextMenu
    // for a context menu, from WM_EXITMENULOOP for the bar's.
    private void ShowContextMenu(ContextMenuSession session, ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options)
    {
        if (_contextMenu is not null || _inMenuLoop)
        {
            _contextMenu?.Dismiss();
            _nextContextMenu?.Session.Complete(null);
            _nextContextMenu = (session, items, options);
            CancelMenuMode();
            return;
        }
        TrackContextMenu(session, items, options);
        ShowNextContextMenu();
    }

    private void ShowNextContextMenu()
    {
        while (_nextContextMenu is { } next && !_inMenuLoop && _contextMenu is null)
        {
            _nextContextMenu = null;
            TrackContextMenu(next.Session, next.Items, next.Options);
        }
    }

    private void TrackContextMenu(ContextMenuSession session, ImmutableArray<DesktopMenuEntry> items, DesktopContextMenuOptions options)
    {
        if (session.IsDismissed || _window == 0 || _destroyAfterMenu)
        {
            session.Complete(null);
            return;
        }
        var rightToLeft = IsRightToLeft;
        var model = WindowsMenuModel.ForContextMenu(items, rightToLeft);
        var menu = CreateMenu(model.Entries, popup: true, popups: null);
        Volatile.Write(ref _contextMenu, session);
        uint chosen;
        try
        {
            var (x, y) = ContextMenuPoint(options);
            var flags = TpmReturnCommand | TpmRightButton
                | (Native.GetSystemMetrics(SmMenuDropAlignment) != 0 ? TpmRightAlign : 0)
                // Mirrors the menu: submenus open to the left and check marks sit on the right.
                | (rightToLeft ? TpmLayoutRtl : 0);
            chosen = (uint)Native.TrackPopupMenuEx(menu, flags, x, y, _window, 0);
        }
        finally
        {
            Volatile.Write(ref _contextMenu, null);
            Native.DestroyMenu(menu);
        }
        // A disabled item cannot be chosen; an id of no item is no choice.
        session.Complete(!session.IsDismissed && model.Items.TryGetValue(chosen, out var entry) && entry is { Planned.Enabled: true, Item: { } item }
            ? item
            : null);
    }

    // Screen pixels: the pointer, or the page point. CSS pixels times the page zoom are device-independent pixels, which
    // the window's DPI scales; the page is laid out left to right from the client area's left edge also in a mirrored
    // window (#389).
    private (int X, int Y) ContextMenuPoint(DesktopContextMenuOptions options)
    {
        if (options is not { X: { } x, Y: { } y } || _controller is null)
        {
            Native.GetCursorPos(out var cursor);
            return (cursor.X, cursor.Y);
        }
        var zoom = _controller.ZoomFactor;
        if (!double.IsFinite(zoom) || zoom <= 0) zoom = 1;
        var scale = zoom * Native.GetDpiForWindow(_window) / 96d;
        Native.GetClientRect(_window, out var client);
        // Both ends of the client area, as a mirrored window maps them in reverse.
        var origin = new Point();
        var end = new Point { X = client.Right };
        Native.ClientToScreen(_window, ref origin);
        Native.ClientToScreen(_window, ref end);
        var left = Math.Min(origin.X, end.X);
        return ((int)Math.Round(left + Math.Clamp(x * scale, 0, client.Right)), (int)Math.Round(origin.Y + Math.Clamp(y * scale, 0, client.Bottom)));
    }

    private const uint MiimState = 0x1, MiimId = 0x2, MiimSubMenu = 0x4, MiimString = 0x40, MiimFType = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MenuItemInfo
    {
        internal uint Size;
        internal uint Mask;
        internal uint Type;
        internal uint State;
        internal uint Id;
        internal nint SubMenu;
        internal nint Checked;
        internal nint Unchecked;
        internal nuint Data;
        internal nint TypeData;
        internal uint Count;
        internal nint Item;
    }

    private static partial class Native
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint CreateMenu();

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint CreatePopupMenu();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyMenu(nint menu);

        [LibraryImport("user32.dll", EntryPoint = "InsertMenuItemW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool InsertMenuItem(nint menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, in MenuItemInfo info);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetMenu(nint window, nint menu);

        [LibraryImport("user32.dll")]
        internal static partial nint GetMenu(nint window);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DrawMenuBar(nint window);

        [LibraryImport("user32.dll")]
        internal static partial int EnableMenuItem(nint menu, uint item, uint flags);

        [LibraryImport("user32.dll")]
        internal static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetCursorPos(out Point point);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ClientToScreen(nint window, ref Point point);

        [LibraryImport("user32.dll")]
        internal static partial short GetKeyState(int key);
    }
}
