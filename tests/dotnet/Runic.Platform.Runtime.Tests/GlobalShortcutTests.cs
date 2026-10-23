using System.Collections.Immutable;
using Runic.Platform.MacOS;
using Runic.Platform.Windows;
using TUnit.Core;

namespace Runic.Platform.Runtime.Tests;

// The portable rules of global shortcuts: the set's validation, the unavailable service, the shared key tables and how
// Windows and macOS map a shortcut for a keyboard layout, with the layout replaced by a table. Registering keys is the
// native cases' (Runic.Platform.Windows.Tests NativeGlobalShortcuts, NativeAppKit here); nothing here touches the session.
internal sealed class GlobalShortcutTests
{
    private static DesktopGlobalShortcut Action(string id, string? shortcut = null) =>
        new(id, "Action " + id) { PreferredShortcut = shortcut is null ? null : DesktopShortcut.Parse(shortcut) };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Rejects(ImmutableArray<DesktopGlobalShortcut> set, string what)
    {
        try { DesktopGlobalShortcutRules.Validate(set); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("accepted " + what);
    }

    [Test]
    public void SetRulesRejectCallerMistakes()
    {
        DesktopGlobalShortcutRules.Validate([]);
        DesktopGlobalShortcutRules.Validate([Action("show"), Action("app.quick-note_2", "Primary+Shift+N")]);
        DesktopGlobalShortcutRules.Validate([.. Enumerable.Range(0, 32).Select(static index => Action("a" + index))]);
        DesktopGlobalShortcutRules.Validate([Action(new string('x', 64))]);
        Rejects(default, "a default array");
        Rejects([.. Enumerable.Range(0, 33).Select(static index => Action("a" + index))], "33 actions");
        Rejects([Action("show"), Action("show")], "a repeated id");
        Rejects([Action("")], "an empty id");
        Rejects([Action(new string('x', 65))], "an id of 65 characters");
        Rejects([Action("show me")], "an id with a space");
        Rejects([Action("é")], "a non-ASCII id");
        Rejects([new DesktopGlobalShortcut("show", " ")], "a blank description");
        Rejects([new DesktopGlobalShortcut("show", new string('d', 257))], "a description of 257 characters");
        Rejects([new DesktopGlobalShortcut("show", "a\0b")], "a description with NUL");
        Rejects([null!], "a null action");
    }

    [Test]
    public void BindingsReportTheRequestedKey()
    {
        var shortcut = DesktopShortcut.Parse("Primary+Shift+S");
        Check(DesktopGlobalShortcutRules.Binding("a", shortcut, DesktopGlobalShortcutStatus.Bound, macOS: false)
            is { Id: "a", Status: DesktopGlobalShortcutStatus.Bound, TriggerDescription: "Ctrl+Shift+S" } bound && bound.Shortcut == shortcut,
            "a bound key reports itself and the Windows text");
        Check(DesktopGlobalShortcutRules.Binding("a", shortcut, DesktopGlobalShortcutStatus.Conflict, macOS: true)
            is { Status: DesktopGlobalShortcutStatus.Conflict, TriggerDescription: "⇧⌘S" }, "a conflict reports the requested key in macOS text");
        Check(DesktopGlobalShortcutRules.Binding("a", null, DesktopGlobalShortcutStatus.Refused, macOS: false)
            is { Status: DesktopGlobalShortcutStatus.Unbound, Shortcut: null, TriggerDescription: null }, "no key is unbound");
        Check(DesktopGlobalShortcutRules.Requested(new DesktopGlobalShortcut("a", "A") { PreferredShortcut = default(DesktopShortcut) }) is null,
            "the default shortcut is no key");
    }

    [Test]
    public async Task UnavailableShortcutsReportTheReason()
    {
        var shortcuts = DesktopGlobalShortcuts.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        Check(DesktopServiceStatus.Get(shortcuts) is CapabilityStatus.Unavailable(PlatformUnavailableReason.ProviderNotConfigured),
            "the status reports the reason");
        Check(await shortcuts.BindAsync([Action("show", "Primary+Alt+S")])
            is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured), "bind");
        Check(await shortcuts.ListAsync() is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Unavailable, "list");
        Check(await shortcuts.ConfigureAsync() is PlatformResult<PlatformUnit>.Unavailable, "configure");
        var threw = false;
        try { await shortcuts.BindAsync([Action("show"), Action("show")]); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "an invalid set still throws");
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            threw = false;
            try { await shortcuts.ListAsync(cancelled.Token); }
            catch (OperationCanceledException) { threw = true; }
            Check(threw, "cancellation throws");
        }
        await shortcuts.DisposeAsync();
        threw = false;
        try { await shortcuts.ListAsync(); }
        catch (ObjectDisposedException) { threw = true; }
        Check(threw, "use after disposal throws");
        try
        {
            DesktopGlobalShortcuts.Unavailable((PlatformUnavailableReason)99);
            throw new InvalidOperationException("accepted an undefined reason");
        }
        catch (ArgumentOutOfRangeException) { }
    }

    [Test]
    public void KeyTablesGiveEachPlatformsForm()
    {
        Check(DesktopShortcutKeys.VirtualKey("S") == 'S' && DesktopShortcutKeys.VirtualKey("F12") == 0x7B
            && DesktopShortcutKeys.VirtualKey("Plus") == 0xBB && DesktopShortcutKeys.VirtualKey("!") is null, "Win32 virtual keys");
        Check(DesktopShortcutKeys.MacKeyCode("A") == 0x00 && DesktopShortcutKeys.MacKeyCode("F1") == 0x7A
            && DesktopShortcutKeys.MacKeyCode("ArrowUp") == 0x7E && DesktopShortcutKeys.MacKeyCode("Delete") == 0x75
            && DesktopShortcutKeys.MacKeyCode("Backspace") == 0x33 && DesktopShortcutKeys.MacKeyCode("F21") is null, "macOS key codes");
        // Every key DesktopShortcut accepts except F21-F24 has a Mac key code, and every key has a Win32 code or a character.
        string[] named = ["Enter", "Escape", "Tab", "Space", "Backspace", "Delete", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
            "Home", "End", "PageUp", "PageDown", "Plus"];
        foreach (var key in named.Concat(Enumerable.Range(1, 20).Select(static f => "F" + f))
                     .Concat(Enumerable.Range('A', 26).Select(static c => ((char)c).ToString())).Concat(Enumerable.Range(0, 10).Select(static d => ((char)('0' + d)).ToString())))
        {
            Check(DesktopShortcutKeys.MacKeyCode(key) is not null || key == "Plus", "no macOS key code for " + key);
            Check(DesktopShortcutKeys.VirtualKey(key) is not null, "no virtual key for " + key);
            Check(DesktopShortcutKeys.KeysymName(key) is not null, "no keysym for " + key);
        }
        Check(DesktopShortcutKeys.HotKeyModifiers(DesktopShortcutModifiers.Control | DesktopShortcutModifiers.Shift)
            == (DesktopShortcutKeys.ModControl | DesktopShortcutKeys.ModShift), "Control is Ctrl for RegisterHotKey");
        Check(DesktopShortcutKeys.CarbonModifiers(DesktopShortcutModifiers.Primary | DesktopShortcutModifiers.Control | DesktopShortcutModifiers.Alt)
            == (DesktopShortcutKeys.CarbonCommand | DesktopShortcutKeys.CarbonControl | DesktopShortcutKeys.CarbonOption), "Carbon modifiers");
        Check(DesktopShortcutKeys.XdgTrigger(DesktopShortcut.Parse("Primary+Shift+S")) == "CTRL+SHIFT+s"
            && DesktopShortcutKeys.XdgTrigger(DesktopShortcut.Parse("Control+Alt+Enter")) == "CTRL+ALT+Return"
            && DesktopShortcutKeys.XdgTrigger(DesktopShortcut.Parse("Primary+,")) == "CTRL+comma"
            && DesktopShortcutKeys.XdgTrigger(DesktopShortcut.Parse("Primary+Plus")) == "CTRL+plus"
            && DesktopShortcutKeys.XdgTrigger(DesktopShortcut.Parse("F5")) == "F5", "XDG shortcuts-spec triggers");
        Check(DesktopShortcutKeys.ReservedBy(DesktopShortcut.Parse("Control+C"), macOS: false) == "Copy"
            && DesktopShortcutKeys.ReservedBy(DesktopShortcut.Parse("Control+C"), macOS: true) is null
            && DesktopShortcutKeys.ReservedBy(DesktopShortcut.Parse("Primary+Q"), macOS: true) == "Quit", "reserved shortcuts per platform");
    }

    // A US layout for VkKeyScanEx: '/' is VK_OEM_2, '?' is Shift+VK_OEM_2, '@' is Shift+2, and '€' needs AltGr.
    private static short UsKeyScan(char character) => character switch
    {
        '/' => 0xBF,
        '?' => 0x1BF,
        '+' => 0x1BB,
        ',' => 0xBC,
        '€' => 0x635,
        _ => -1,
    };

    [Test]
    public void WindowsMapsShortcutsForTheLayout()
    {
        Check(WindowsHotKeys.Map(DesktopShortcut.Parse("Primary+Shift+S"), UsKeyScan) == new WindowsHotKeys.HotKey('S',
            DesktopShortcutKeys.ModControl | DesktopShortcutKeys.ModShift), "a letter keeps its virtual key");
        Check(WindowsHotKeys.Map(DesktopShortcut.Parse("Control+Alt+F9"), UsKeyScan) == new WindowsHotKeys.HotKey(0x78,
            DesktopShortcutKeys.ModControl | DesktopShortcutKeys.ModAlt), "a function key");
        Check(WindowsHotKeys.Map(DesktopShortcut.Parse("Primary+?"), UsKeyScan) == new WindowsHotKeys.HotKey(0xBF,
            DesktopShortcutKeys.ModControl | DesktopShortcutKeys.ModShift), "a shifted character adds Shift");
        Check(WindowsHotKeys.Map(DesktopShortcut.Parse("Primary+Plus"), UsKeyScan) == new WindowsHotKeys.HotKey(0xBB,
            DesktopShortcutKeys.ModControl | DesktopShortcutKeys.ModShift), "Plus is the character +");
        Check(WindowsHotKeys.Map(DesktopShortcut.Parse("Primary+/"), static _ => -1) is null, "a character the layout lacks is refused");
        Check(WindowsHotKeys.Map(new DesktopShortcut("!"), UsKeyScan) is null, "a character the layout scan does not find is refused");
        Check(WindowsHotKeys.Map(DesktopShortcut.Parse("Primary+Alt+F12"), UsKeyScan) is null, "F12 stays the debugger's");
        Check(WindowsHotKeys.Map(DesktopShortcut.Parse("Control+C"), UsKeyScan) is null
            && WindowsHotKeys.Map(DesktopShortcut.Parse("Alt+F4"), UsKeyScan) is null, "editing and window shortcuts are refused");
    }

    [Test]
    public void WindowsRefusesCharactersThatNeedAltGr()
    {
        // On a German layout @ is AltGr+Q: Ctrl and Alt in the shift state (0x651).
        Check(WindowsHotKeys.Map(new DesktopShortcut("@"), static _ => 0x651) is null, "AltGr in the shift state is refused");
        Check(WindowsHotKeys.Map(new DesktopShortcut("@", DesktopShortcutModifiers.Primary | DesktopShortcutModifiers.Shift),
            static _ => 0x132) == new WindowsHotKeys.HotKey(0x32, DesktopShortcutKeys.ModControl | DesktopShortcutKeys.ModShift),
            "Shift in the shift state joins a declared Shift");
    }

    // An AZERTY-like layout: a is the ANSI Q key, ? needs Shift on the M key's neighbour, and / needs Shift.
    private static (ushort Code, bool Shift)? Azerty(char character) => character switch
    {
        'a' => (0x0C, false),
        'q' => (0x00, false),
        ',' => (0x2E, false),
        '/' => (0x2F, true),
        _ => null,
    };

    [Test]
    public void MacOSMapsShortcutsForTheLayout()
    {
        uint command = DesktopShortcutKeys.CarbonCommand, shift = DesktopShortcutKeys.CarbonShift, option = DesktopShortcutKeys.CarbonOption;
        Check(MacGlobalShortcuts.Map(DesktopShortcut.Parse("Primary+Alt+A"), Azerty) == (0x0Cu, command | option),
            "a letter is the key that types it");
        Check(MacGlobalShortcuts.Map(DesktopShortcut.Parse("Primary+Alt+Z"), Azerty) == (0x06u, command | option),
            "a letter the layout search misses keeps its ANSI key");
        Check(MacGlobalShortcuts.Map(DesktopShortcut.Parse("Primary+/"), Azerty) == (0x2Fu, command | shift),
            "a shifted character adds Shift");
        Check(MacGlobalShortcuts.Map(DesktopShortcut.Parse("Primary+1"), Azerty) == (0x12u, command),
            "digits keep the top-row keys");
        Check(MacGlobalShortcuts.Map(DesktopShortcut.Parse("Control+F5"), Azerty) == (0x60u, DesktopShortcutKeys.CarbonControl),
            "named keys have fixed codes");
        Check(MacGlobalShortcuts.Map(DesktopShortcut.Parse("Primary+;"), Azerty) is null, "a missing character is refused");
        Check(MacGlobalShortcuts.Map(DesktopShortcut.Parse("Primary+Plus"), static c => c == '+' ? (0x18, true) : null)
            == (0x18u, command | shift), "Plus is the character +");
        Check(MacGlobalShortcuts.Map(DesktopShortcut.Parse("F21"), Azerty) is null, "F21 has no Mac key");
        Check(MacGlobalShortcuts.Map(DesktopShortcut.Parse("Alt+K"), Azerty) == (0x28u, option),
            "Option alone is left to RegisterEventHotKey, which macOS 26 accepts");
        Check(MacGlobalShortcuts.Map(DesktopShortcut.Parse("Primary+Q"), Azerty) is null
            && MacGlobalShortcuts.Map(DesktopShortcut.Parse("Primary+Shift+Z"), Azerty) is null,
            "the standard menus' shortcuts are refused");
    }
}
