using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Runic.Platform.Runtime;
using TUnit.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Model = Runic.Platform.Windows.WindowsStatusIconModel;

namespace Runic.Platform.Windows.Tests;

// The portable cases check the menu, tooltip and icon conversion without Win32. The NativeStatusIcon case creates a
// real notification-area icon and drives its window; NativePresentation restores and raises a real window.
internal sealed partial class StatusIconTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static ImmutableArray<DesktopMenuEntry> Menu(bool checkedDaily, params DesktopMenuItem[] extra) =>
    [
        .. extra,
        new DesktopMenuItem("Open & show", "open"),
        new DesktopMenuItem("Check daily", "daily") { IsChecked = checkedDaily },
        new DesktopMenuSeparator(),
        new DesktopMenu("More", [new DesktopMenuItem("About", "about"), new DesktopMenuItem("Off", "off") { IsEnabled = false }]),
        new DesktopMenuItem("Quit", "quit"),
    ];

    [Test]
    public void MenuCommandsKeepTheirIds()
    {
        var ids = new Model.Ids();
        var first = Model.BuildMenu(Menu(false), ids);
        var open = first.Commands.Single(c => c.Value.CommandId == "open").Key;
        var about = first.Commands.Single(c => c.Value.CommandId == "about").Key;
        Check(open != 0 && first.Commands.Count == 5, "every command has a nonzero ID");
        Check(first.Commands.Keys.Distinct().Count() == 5, "IDs are distinct");
        // A new entry before the others, and a changed check state, leave the existing IDs alone.
        var second = Model.BuildMenu(Menu(true, new DesktopMenuItem("New", "new")), ids);
        Check(second.Commands.Single(c => c.Value.CommandId == "open").Key == open
            && second.Commands.Single(c => c.Value.CommandId == "about").Key == about, "IDs stay with their commands");
        Check(second.Commands.Single(c => c.Value.CommandId == "new").Key > about, "a new command gets a new ID");
        // A choice in the first menu still resolves against the menu that was shown.
        Check(first.Choose(open) == "open" && second.Choose(open) == "open", "the chosen ID resolves to its command");
        Check(first.Choose(first.Commands.Single(c => c.Value.CommandId == "off").Key) is null, "a disabled command is never chosen");
        Check(first.Choose(9999) is null && first.Choose(0) is null, "an unknown ID chooses nothing");
        Check(Model.BuildMenu([], ids).IsEmpty, "an empty menu shows none");
    }

    [Test]
    public void MenuMirrorsTheItems()
    {
        var menu = Model.BuildMenu(Menu(true), new Model.Ids());
        Check(menu.Entries is [Model.Command { Label: "Open && show", Checked: null }, Model.Command { Label: "Check daily", Checked: true },
            Model.Separator, Model.Submenu { Label: "More", Enabled: true, Entries: [Model.Command { CommandId: "about" }, Model.Command { Enabled: false }] },
            Model.Command { CommandId: "quit" }], "entries, labels with '&' escaped, check state, submenu and enabled state");
    }

    [Test]
    public void TooltipFitsSzTip()
    {
        Check(Model.Tooltip("Updates", null) == "Updates" && Model.Tooltip("Updates", "") == "Updates", "a title alone");
        Check(Model.Tooltip("Updates", "3 pending") == "Updates\n3 pending", "the tooltip below the title");
        string cut = Model.Tooltip("Title", new string('x', 1000));
        Check(cut.Length == 127 && cut.StartsWith("Title\n", StringComparison.Ordinal), "cut to 127 characters and the NUL");
        string emoji = Model.Tooltip(new string('a', 126), "\U0001F680");
        Check(emoji.Length == 127 && !char.IsHighSurrogate(emoji[^1]) && emoji.EndsWith('\n'), "a surrogate pair is never split");
    }

    [Test]
    public void IconImagesConvertToBgra()
    {
        var bgra = new byte[8];
        Model.ToBgra([1, 2, 3, 4, 10, 20, 30, 40], bgra);
        Check(bgra.SequenceEqual(new byte[] { 3, 2, 1, 4, 30, 20, 10, 40 }), "red and blue swap, alpha stays straight");

        DesktopIconImage Image(int side) => new(side, side, [.. new byte[side * side * 4]]);
        var icon = new DesktopIcon { Images = [Image(48), Image(16), Image(24), Image(32)] };
        Check(Model.SelectImage(icon, 20)!.Width == 24, "the smallest image at least as large as the small icon");
        Check(Model.SelectImage(icon, 64)!.Width == 48, "else the largest");
        Check(Model.SelectImage(icon, 16)!.Width == 16, "an exact size");
        Check(Model.SelectImage(new DesktopIcon { IconName = "applications-system" }, 16) is null, "theme names are ignored");

        var normal = new DesktopIcon { Images = [Image(16)] };
        var attention = new DesktopIcon { Images = [Image(32)] };
        var state = new DesktopStatusIconState(normal, "Title") { AttentionIcon = attention };
        Check(Model.ShownIcon(state) == normal && Model.ShownIcon(state with { Status = DesktopStatusIconStatus.Passive }) == normal, "the normal icon");
        Check(Model.ShownIcon(state with { Status = DesktopStatusIconStatus.NeedsAttention }) == attention, "the attention icon stands in for the attention state");
        Check(Model.ShownIcon(state with { Status = DesktopStatusIconStatus.NeedsAttention, AttentionIcon = null }) == normal, "or the normal icon without one");
    }

    [Test]
    public unsafe void NotifyIconDataHasTheX64Layout()
    {
#pragma warning disable CA1416 // Layout only; nothing is called.
        if (IntPtr.Size == 8) Check(sizeof(WindowsShellInterop.NOTIFYICONDATAW) == 976, "NOTIFYICONDATAW is 976 bytes on x64 and ARM64");
        if (IntPtr.Size == 8) Check(sizeof(WindowsShellInterop.SHELLEXECUTEINFOW) == 112, "SHELLEXECUTEINFOW is 112 bytes on x64 and ARM64");
#pragma warning restore CA1416
    }

    [Test, Explicit, Category("NativeStatusIcon")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    public async Task NativeStatusIconAsync()
    {
        CheckNativeMenuAndIcon();
        bool shell = FindWindowW("Shell_TrayWnd", null) != 0;
        Console.WriteLine(shell ? "The session has a taskbar (Shell_TrayWnd)." : "The session has no taskbar; the icon cannot appear.");
        await using var icon = (WindowsStatusIcon)WindowsPlatformProvider.CreateStatusIcon();
        var activations = Channel.CreateUnbounded<DesktopStatusIconActivation>();
        icon.Activated += (_, activation) => activations.Writer.TryWrite(activation);
        Check(await icon.HideAsync() is PlatformResult<PlatformUnit>.Success && icon.Window == 0, "hiding before showing creates nothing");

        var image = new DesktopIconImage(16, 16, [.. Enumerable.Range(0, 16 * 16).SelectMany(_ => new byte[] { 0x30, 0x90, 0xE0, 0xFF })]);
        var red = new DesktopIconImage(32, 32, [.. Enumerable.Range(0, 32 * 32).SelectMany(_ => new byte[] { 0xE0, 0x20, 0x20, 0xFF })]);
        var state = new DesktopStatusIconState(new DesktopIcon { IconName = "applications-system", Images = [image] }, "Runic status icon test")
        {
            Tooltip = new string('t', 400),
            AttentionIcon = new DesktopIcon { Images = [red] },
            Menu = Menu(false),
        };
        var shown = await icon.ShowAsync(state);
        Console.WriteLine($"ShowAsync: {shown}");
        Check(shown is PlatformResult<PlatformUnit>.Success || !shell && shown is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "the icon is added where there is a taskbar, and reported unavailable where there is none");
        nint window = icon.Window;
        Check(window != 0, "the icon has its window");
        Check(await icon.ShowAsync(state with { Status = DesktopStatusIconStatus.NeedsAttention }) is var attention
            && attention.GetType() == shown.GetType(), "the attention icon replaces the icon");
        Check(await icon.ShowAsync(state with { Icon = new DesktopIcon { IconName = "only-a-theme-name" } }) is var themed
            && themed.GetType() == shown.GetType(), "a theme name alone shows the application's icon");
        Check(await icon.ShowAsync(state) is var again && again.GetType() == shown.GetType(), "showing again modifies the icon");

        // The shell's callbacks, as NOTIFYICON_VERSION_4 sends them: the event in the low word of lParam.
        Post(window, WindowsStatusIcon.CallbackMessage, 0, WindowsShellInterop.NIN_SELECT | WindowsStatusIcon.IconId << 16);
        Check(await Next(activations) is { Kind: DesktopStatusIconActivationKind.Primary, CommandId: null }, "a click is the primary action");
        Post(window, WindowsStatusIcon.CallbackMessage, 0, WindowsShellInterop.NIN_KEYSELECT | WindowsStatusIcon.IconId << 16);
        Check(await Next(activations) is { Kind: DesktopStatusIconActivationKind.Primary }, "Space or Enter is the primary action");
        Post(window, WindowsStatusIcon.CallbackMessage, 0, PInvoke.WM_MBUTTONUP | WindowsStatusIcon.IconId << 16);
        Check(await Next(activations) is { Kind: DesktopStatusIconActivationKind.Secondary }, "a middle click is the secondary action");

        // The context menu opens at the anchor point; the first entry is chosen with the keyboard.
        Post(window, WindowsStatusIcon.CallbackMessage, 200 | 200 << 16, PInvoke.WM_CONTEXTMENU | WindowsStatusIcon.IconId << 16);
        nint popup = await WaitForAsync(() => FindWindowW("#32768", null));
        Check(popup != 0, "the context menu opens");
        Post(popup, WM_KEYDOWN, VK_DOWN, 0);
        Post(popup, WM_KEYDOWN, VK_RETURN, 0);
        Check(await Next(activations) is { Kind: DesktopStatusIconActivationKind.MenuItem, CommandId: "open" }, "a menu choice raises its command");
        Check(await WaitForAsync(() => FindWindowW("#32768", null) == 0 ? 1 : 0) == 1, "the menu closes");

        if (shown is PlatformResult<PlatformUnit>.Success)
        {
            // Explorer restarting forgets the icon; TaskbarCreated adds it again.
            Check(Notify(WindowsShellInterop.NIM_DELETE, window) != 0, "the test removes the icon as a restarting Explorer does");
            Check(Notify(WindowsShellInterop.NIM_MODIFY, window) == 0, "the icon is gone");
            Post(window, WindowsStatusIcon.s_taskbarCreated, 0, 0);
            Check(await WaitForAsync(() => Notify(WindowsShellInterop.NIM_MODIFY, window)) != 0, "TaskbarCreated adds the icon again");
        }
        else Console.WriteLine("Not checked without a taskbar: adding the icon again on TaskbarCreated.");

        bool added = shown is PlatformResult<PlatformUnit>.Success;
        // Enter can arrive as two NIN_KEYSELECT; one key press is one primary action.
        await Task.Delay(TimeSpan.FromMilliseconds(WindowsStatusIcon.KeySelectRepeatMilliseconds * 2));
        Post(window, WindowsStatusIcon.CallbackMessage, 0, WindowsShellInterop.NIN_KEYSELECT | WindowsStatusIcon.IconId << 16);
        Post(window, WindowsStatusIcon.CallbackMessage, 0, WindowsShellInterop.NIN_KEYSELECT | WindowsStatusIcon.IconId << 16);
        Check(await Next(activations) is { Kind: DesktopStatusIconActivationKind.Primary }, "a repeated key select is one primary action");
        await Task.Delay(TimeSpan.FromMilliseconds(WindowsStatusIcon.KeySelectRepeatMilliseconds * 2));
        Check(!activations.Reader.TryRead(out _), "the repeat raised nothing");

        Check(await icon.HideAsync() is PlatformResult<PlatformUnit>.Success, "hiding removes the icon");
        Check(await icon.ShowAsync(state) is var shownAgain && shownAgain.GetType() == shown.GetType(), "showing after hiding adds it again");

        // WM_CLOSE from another process (taskkill without /F, Restart Manager) leaves the icon to the application.
        Post(window, PInvoke.WM_CLOSE, 0, 0);
        await Task.Delay(500);
        Check(PInvoke.IsWindow((HWND)window) && icon.Window == window && (!added || Notify(WindowsShellInterop.NIM_MODIFY, window) != 0),
            "an outside WM_CLOSE is ignored");

        // The session ending destroys the window and removes the icon; the next ShowAsync starts a new window.
        Post(window, PInvoke.WM_ENDSESSION, 1, 0);
        Check(await WaitForAsync(() => PInvoke.IsWindow((HWND)window) ? 0 : 1) == 1 && icon.Window == 0, "the session's end destroys the window");
        Check(!added || Notify(WindowsShellInterop.NIM_MODIFY, window) == 0, "and removes the icon");
        Check(await icon.ShowAsync(state) is var restarted && restarted.GetType() == shown.GetType(), "ShowAsync starts again");
        nint second = icon.Window;
        Check(second != 0 && second != window, "with a new window");
        Post(second, WindowsStatusIcon.CallbackMessage, 0, WindowsShellInterop.NIN_SELECT | WindowsStatusIcon.IconId << 16);
        Check(await Next(activations) is { Kind: DesktopStatusIconActivationKind.Primary }, "the new window raises activations");

        await icon.DisposeAsync().AsTask().WaitAsync(Wait);
        Check(await WaitForAsync(() => PInvoke.IsWindow((HWND)second) ? 0 : 1) == 1, "disposal destroys the window");
        Check(!added || Notify(WindowsShellInterop.NIM_MODIFY, second) == 0, "disposal removes the icon");
        Console.WriteLine($"PASS native Windows status icon ({(added ? "in the notification area" : "without a taskbar")}): "
            + "add, modify, attention icon, primary, secondary, menu choice" + (added ? ", TaskbarCreated" : "")
            + ", repeated key select, outside WM_CLOSE, session end and restart, hide and disposal");
    }

    // The icon's own entry, as Explorer knows it.
    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static unsafe int Notify(uint message, nint window)
    {
        var data = new WindowsShellInterop.NOTIFYICONDATAW
        { cbSize = (uint)sizeof(WindowsShellInterop.NOTIFYICONDATAW), hWnd = window, uID = WindowsStatusIcon.IconId };
        return WindowsShellInterop.Shell_NotifyIcon(message, &data);
    }

    // The native menu mirrors the model, and RGBA images become icons.
    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static unsafe void CheckNativeMenuAndIcon()
    {
        var model = Model.BuildMenu(Menu(true), new Model.Ids());
        var menu = WindowsStatusIcon.CreateMenu(model.Entries);
        try
        {
            Check(GetMenuItemCount(menu) == 5, "five top-level entries");
            uint daily = model.Commands.Single(c => c.Value.CommandId == "daily").Key;
            Check((GetMenuState(menu, daily, 0) & (uint)MENU_ITEM_FLAGS.MF_CHECKED) != 0, "the check item is checked");
            Check((GetMenuState(menu, 2, 0x400) & (uint)MENU_ITEM_FLAGS.MF_SEPARATOR) != 0, "the separator");
            nint more = GetSubMenu(menu, 3);
            Check(more != 0 && GetMenuItemCount(more) == 2, "the submenu holds its entries");
            uint off = model.Commands.Single(c => c.Value.CommandId == "off").Key;
            Check((GetMenuState(more, off, 0) & (uint)MENU_ITEM_FLAGS.MF_GRAYED) != 0, "a disabled entry is grayed");
        }
        finally { PInvoke.DestroyMenu(menu); }
        var icon = WindowsStatusIcon.FromImage(new DesktopIconImage(2, 1, [255, 0, 0, 255, 0, 0, 255, 128]));
        Check(icon != default, "CreateIconIndirect makes an icon from RGBA");
        PInvoke.DestroyIcon(icon);
    }

    [Test, Explicit, Category("NativePresentation")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    public async Task NativePresentationAsync()
    {
        // A real top-level window on this thread; ShowWindow and SetForegroundWindow act on it directly.
        var window = CreateTestWindow();
        Check(window != default, "the window is created");
        try
        {
            PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_MINIMIZE);
            Check(PInvoke.IsIconic(window), "the window is minimized");
            var result = await WindowsWindowPresentation.PresentAsync(new InlineOwner(window), CancellationToken.None);
            Console.WriteLine($"PresentAsync: {result}");
            Check(!PInvoke.IsIconic(window), "presenting restores the window");
            Check(result is PlatformResult<PlatformUnit>.Success && PInvoke.GetForegroundWindow() == window
                || result is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied } failed
                && failed.Diagnostic == WindowsWindowPresentation.Flashed, "the window comes forward, or its taskbar button flashes and the result says so");
            Console.WriteLine(result is PlatformResult<PlatformUnit>.Success
                ? "PASS native Windows presentation: restored and brought to the foreground"
                : "PASS native Windows presentation: restored; Windows refused the foreground and the taskbar button flashes");
            PInvoke.DestroyWindow(window);

            // A close-to-tray application hides its window; presenting shows it again.
            var hidden = CreateTestWindow(visible: false);
            try
            {
                Check(!PInvoke.IsWindowVisible(hidden), "the window starts hidden");
                var shown = await WindowsWindowPresentation.PresentAsync(new InlineOwner(hidden), CancellationToken.None);
                Console.WriteLine($"PresentAsync (hidden window): {shown}");
                Check(PInvoke.IsWindowVisible(hidden) && !PInvoke.IsIconic(hidden), "presenting shows a hidden window");
                Check(shown is PlatformResult<PlatformUnit>.Success || shown is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied },
                    "the hidden window comes forward, or its taskbar button flashes");
            }
            finally { PInvoke.DestroyWindow(hidden); }
            var closed = await WindowsWindowPresentation.PresentAsync(new InlineOwner(default), CancellationToken.None);
            Check(closed is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed), "a closed owner is reported closed");
        }
        finally { if (PInvoke.IsWindow(window)) PInvoke.DestroyWindow(window); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static unsafe HWND CreateTestWindow(bool visible = true)
    {
        fixed (char* className = "STATIC")
        fixed (char* title = "Runic presentation test")
            return PInvoke.CreateWindowEx(0, className, title, WINDOW_STYLE.WS_OVERLAPPEDWINDOW | (visible ? WINDOW_STYLE.WS_VISIBLE : 0),
                100, 100, 320, 200, default, default, default, null);
    }

    // Dispatches inline: the test thread owns the window.
    private sealed class InlineOwner(nint window) : INativeWindowOwner
    {
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => window != 0;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
        {
            if (window == 0) throw new OwnerClosedException();
            action(window);
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<DesktopStatusIconActivation> Next(Channel<DesktopStatusIconActivation> activations) =>
        await activations.Reader.ReadAsync().AsTask().WaitAsync(Wait);

    private static async Task<nint> WaitForAsync(Func<nint> probe)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            nint value = probe();
            if (value != 0 || DateTime.UtcNow > deadline) return value;
            await Task.Delay(50);
        }
    }

    private static void Post(nint window, uint message, nuint wParam, long lParam)
    {
#pragma warning disable CA1416 // Called only from the Windows cases.
        Check(PInvoke.PostMessage((HWND)window, message, (WPARAM)wParam, (LPARAM)(nint)lParam), $"message 0x{message:X} posted");
#pragma warning restore CA1416
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private const uint WM_KEYDOWN = 0x100;
    private const nuint VK_RETURN = 0x0D, VK_DOWN = 0x28;
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint FindWindowW(string className, string? title);
    [LibraryImport("user32.dll")] private static partial int GetMenuItemCount(nint menu);
    [LibraryImport("user32.dll")] private static partial uint GetMenuState(nint menu, uint item, uint flags);
    [LibraryImport("user32.dll")] private static partial nint GetSubMenu(nint menu, int position);
}
