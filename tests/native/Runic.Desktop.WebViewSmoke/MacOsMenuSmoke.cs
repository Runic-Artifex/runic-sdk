using System.Runtime.InteropServices;
using Runic.Desktop;

// W315-002 and W315-003 on the macOS runner: the application's menus join the standard ones in NSApp.mainMenu, items
// follow their command's availability when AppKit validates them, choosing one or pressing its key equivalent runs the
// command of the key window (or the application's), and the standard Edit, File and application shortcuts stay theirs.
internal static partial class MacOsMenuSmoke
{
    internal static readonly IReadOnlyList<DesktopMenu> Bar =
    [
        new("Settings", [new DesktopMenuBarItem("Settings…", "app.settings") { Shortcut = DesktopShortcut.Parse("Primary+,") }])
        {
            Role = DesktopMenuRole.Application,
        },
        new("Fichier", [new DesktopMenuBarItem("Enregistrer", "file.save") { Shortcut = DesktopShortcut.Parse("Primary+S") }])
        {
            Role = DesktopMenuRole.File,
        },
        new("Tools",
        [
            new DesktopMenuBarItem("Run", "tools.run") { Shortcut = DesktopShortcut.Parse("Primary+Shift+R") },
            new DesktopMenuBarSeparator(),
            new DesktopMenuBarItem("Copy Special", "tools.copy") { Shortcut = DesktopShortcut.Parse("Primary+C") },
            new DesktopMenu("More", [new DesktopMenuBarItem("Nothing", "tools.nothing")]),
        ]),
        new("Help", [new DesktopMenuBarItem("Runic Help", "help.show")]) { Role = DesktopMenuRole.Help },
    ];

    internal sealed class Target : IDesktopMenuCommandTarget
    {
        private readonly HashSet<string> _handled;
        internal Target(params string[] handled) => _handled = [.. handled];
        internal bool Enabled { get; set; } = true;
        internal System.Collections.Concurrent.ConcurrentQueue<string> Executed { get; } = new();
        public bool CanExecute(string commandId) => Enabled && _handled.Contains(commandId);
        public void Execute(string commandId) => Executed.Enqueue(commandId);
    }

    internal static async Task ExerciseAsync(DesktopHost host, DesktopWindow window)
    {
        var windowTarget = new Target("file.save", "tools.run", "tools.copy");
        var applicationTarget = new Target("app.settings", "help.show", "tools.run");
        host.MenuCommands = applicationTarget;
        window.MenuCommands = windowTarget;
        host.SetMenuBar(Bar);
        await window.FocusAsync();
        await Task.Delay(500);
        string? failure = null;
        await window.InvokeNativeAsync(handle =>
        {
            var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
            try { failure = Check(handle, windowTarget, applicationTarget); }
            catch (Exception error) { failure = error.ToString(); }
            finally { Send(pool, Sel("drain")); }
        });
        if (failure is not null) throw new InvalidOperationException("Menu bar: " + failure);
        // The command runs after AppKit returns; the target only records it.
        Console.WriteLine("PASS menu bar: declared menus beside the standard ones, availability, actions and key equivalents.");
        host.MenuCommands = null;
        window.MenuCommands = null;
        host.SetMenuBar([]);
    }

    private static string? Check(nint window, Target windowTarget, Target applicationTarget)
    {
        var application = Send(Class("NSApplication"), Sel("sharedApplication"));
        var mainMenu = Send(application, Sel("mainMenu"));
        Console.WriteLine("MENU " + Describe(mainMenu, 0));
        var titles = Items(mainMenu).Select(item => Text(Send(item, Sel("title")))).ToArray();
        string[] expectedOrder = ["Fichier", "Edit", "View", "Tools", "Window", "Help"];
        if (!titles.Skip(1).SequenceEqual(expectedOrder))
            return $"the menus are [{string.Join(", ", titles)}], not the application menu then [{string.Join(", ", expectedOrder)}]";

        var applicationMenu = Submenu(mainMenu, 0);
        var settings = Find(applicationMenu, "Settings…") ?? 0;
        if (settings == 0 || Text(Send(settings, Sel("keyEquivalent"))) != "," || (ulong)Send(settings, Sel("keyEquivalentModifierMask")) != Command)
            return "the application menu has no Settings… ⌘, item";
        var file = Submenu(mainMenu, 1);
        var close = Find(file, "Close") ?? 0;
        if (Find(file, "Enregistrer") is not { } save || Text(Send(save, Sel("keyEquivalent"))) != "s" || close == 0 || Text(Send(close, Sel("keyEquivalent"))) != "w")
            return "the File menu lacks Enregistrer ⌘S or the standard Close ⌘W";
        var edit = Submenu(mainMenu, 2);
        if (Find(edit, "Copy") is not { } copy || Text(Send(copy, Sel("keyEquivalent"))) != "c")
            return "the Edit menu lost Copy ⌘C";
        var tools = Submenu(mainMenu, 4);
        if (Find(tools, "Copy Special") is not { } special || Text(Send(special, Sel("keyEquivalent"))) != "")
            return "the declared ⌘C item took the Edit menu's shortcut";
        var run = Find(tools, "Run") ?? 0;
        if (Text(Send(run, Sel("keyEquivalent"))) != "r" || (ulong)Send(run, Sel("keyEquivalentModifierMask")) != (Command | Shift))
            return "Run has no ⇧⌘R key equivalent";
        if (Find(Submenu(tools, 3), "Nothing") is not { } nothing)
            return "the Tools submenu is missing";

        // AppKit validates the declared items through their target before showing them.
        Send(tools, Sel("update"));
        Send(Submenu(tools, 3), Sel("update"));
        if (Bool(run, Sel("isEnabled")) == 0 || Bool(nothing, Sel("isEnabled")) != 0)
            return "Run is not enabled, or the unhandled Nothing is";
        windowTarget.Enabled = false;
        Send(tools, Sel("update"));
        // The application's target also runs tools.run.
        if (Bool(run, Sel("isEnabled")) == 0) return "Run did not fall back to the application's command";
        applicationTarget.Enabled = false;
        Send(tools, Sel("update"));
        if (Bool(run, Sel("isEnabled")) != 0) return "Run stayed enabled while no target could run it";
        windowTarget.Enabled = true;
        applicationTarget.Enabled = true;
        Send(tools, Sel("update"));

        var key = Bool(window, Sel("isKeyWindow")) != 0;
        Arg(tools, Sel("performActionForItemAtIndex:"), (nint)0);
        var ran = key ? windowTarget.Executed : applicationTarget.Executed;
        if (!ran.TryDequeue(out var executed) || executed != "tools.run")
            return $"choosing Run did not run the {(key ? "key window's" : "application's")} command";
        if (!key) Console.WriteLine("MENU the window is not key on this runner: the application's target ran the command.");

        // A real ⇧⌘R key event through the menu's key equivalent matching.
        var keyEvent = CreateKeyboardEvent(0, 0x0f, true); // kVK_ANSI_R
        SetFlags(keyEvent, Command | Shift);
        var nsEvent = Arg(Class("NSEvent"), Sel("eventWithCGEvent:"), keyEvent);
        var handled = Arg(mainMenu, Sel("performKeyEquivalent:"), nsEvent) != 0;
        CFRelease(keyEvent);
        if (!handled || !ran.TryDequeue(out executed) || executed != "tools.run")
            return "⇧⌘R did not run Run";
        // ⌘, runs the application's Settings… even with a window open: the window does not handle it.
        var comma = CreateKeyboardEvent(0, 0x2b, true); // kVK_ANSI_Comma
        SetFlags(comma, Command);
        handled = Arg(mainMenu, Sel("performKeyEquivalent:"), Arg(Class("NSEvent"), Sel("eventWithCGEvent:"), comma)) != 0;
        CFRelease(comma);
        if (!handled || !applicationTarget.Executed.TryDequeue(out executed) || executed != "app.settings")
            return "⌘, did not run the application's Settings…";
        return null;
    }

    private static IEnumerable<nint> Items(nint menu)
    {
        for (long index = 0, count = Send(menu, Sel("numberOfItems")); index < count; index++)
            yield return Arg(menu, Sel("itemAtIndex:"), (nint)index);
    }

    private static nint Submenu(nint menu, int index) => Send(Arg(menu, Sel("itemAtIndex:"), index), Sel("submenu"));

    private static nint? Find(nint menu, string title) =>
        menu == 0 ? null : Items(menu).Where(item => Text(Send(item, Sel("title"))) == title).Select(static item => (nint?)item).FirstOrDefault();

    private static string Describe(nint menu, int depth) => menu == 0 ? "(none)" : string.Join("; ", Items(menu).Select(item =>
    {
        var submenu = Send(item, Sel("submenu"));
        return $"'{Text(Send(item, Sel("title")))}' key='{Text(Send(item, Sel("keyEquivalent")))}'" +
            (submenu != 0 && depth < 1 ? " [" + Describe(submenu, depth + 1) + "]" : "");
    }));

    private static string Text(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"))) ?? "";

    private const ulong Command = 0x100000, Shift = 0x20000;
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Arg(nint target, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte Bool(nint target, nint selector);
    [LibraryImport(CoreGraphics, EntryPoint = "CGEventCreateKeyboardEvent")] private static partial nint CreateKeyboardEvent(nint source, ushort key, [MarshalAs(UnmanagedType.U1)] bool down);
    [LibraryImport(CoreGraphics, EntryPoint = "CGEventSetFlags")] private static partial void SetFlags(nint keyEvent, ulong flags);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")] private static partial void CFRelease(nint value);
}
