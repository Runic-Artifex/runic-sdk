using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Runic.Platform;
using Runic.Platform.MacOS;


// Carbon hot keys inside the Desktop event loop: binding, the refused keys, a second registration of the same key in the
// process, and release on rebind and disposal. A key press posted with CGEventPost and macOS's own answer to an
// Option-only hot key are reported, not required: the runner may drop posted events without Accessibility trust.
internal sealed partial class MacAppKitNativeTests
{
    private static readonly DesktopShortcut HotKey = DesktopShortcut.Parse("Primary+Control+Alt+F9");

    private static ImmutableArray<DesktopGlobalShortcut> HotKeySet(params (string Id, DesktopShortcut? Shortcut)[] actions) =>
        [.. actions.Select(static action => new DesktopGlobalShortcut(action.Id, "Runic test " + action.Id) { PreferredShortcut = action.Shortcut })];

    private static async Task<ImmutableArray<DesktopGlobalShortcutBinding>> BindAsync(IDesktopGlobalShortcuts shortcuts,
        ImmutableArray<DesktopGlobalShortcut> set) =>
        await shortcuts.BindAsync(set).AsTask().WaitAsync(Step) is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success(var bindings)
            ? bindings
            : throw new InvalidOperationException("BindAsync did not succeed.");

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static async Task GlobalShortcutsAsync()
    {
        await using var shortcuts = MacOSPlatformProvider.CreateGlobalShortcuts();
        var activations = Channel.CreateUnbounded<string>();
        shortcuts.Activated += (_, activation) => activations.Writer.TryWrite(activation.Id);
        Require(await shortcuts.ConfigureAsync() is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "ConfigureAsync on macOS.");
        var bindings = await BindAsync(shortcuts, HotKeySet(("show", HotKey), ("none", null), ("quit", DesktopShortcut.Parse("Primary+Q")),
            ("option", DesktopShortcut.Parse("Alt+Shift+K")), ("twice", HotKey)));
        Console.WriteLine(string.Join(Environment.NewLine, bindings));
        var optionRefused = OperatingSystem.IsMacOSVersionAtLeast(15);
        Require(bindings is [{ Id: "show", Status: DesktopGlobalShortcutStatus.Bound, TriggerDescription: "⌃⌥⌘F9" },
            { Status: DesktopGlobalShortcutStatus.Unbound }, { Status: DesktopGlobalShortcutStatus.Refused }, var option,
            { Id: "twice", Status: DesktopGlobalShortcutStatus.Conflict }]
            && option.Status == (optionRefused ? DesktopGlobalShortcutStatus.Refused : DesktopGlobalShortcutStatus.Bound),
            "Bindings: one key binds, ⌘Q is refused, Option+Shift alone follows the macOS version and the same key again conflicts.");
        Require(await shortcuts.ListAsync() is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success(var listed)
            && listed.SequenceEqual(bindings), "ListAsync reports the bindings.");
        Pass("Carbon hot keys bind, refuse the standard menus' keys and report a repeated key as a conflict");

        // Finding, not a requirement: whether a posted key press reaches the hot key on this runner.
        PostKeyPress(0x65, CommandFlag | ControlFlag | OptionFlag);
        using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            try { Console.WriteLine($"CGEventPost of ⌃⌥⌘F9 activated '{await activations.Reader.ReadAsync(wait.Token)}'."); }
            catch (OperationCanceledException) { Console.WriteLine("CGEventPost of ⌃⌥⌘F9 did not reach the hot key on this runner."); }
        }
        // Finding: macOS's own answer to an Option+Shift-only hot key, which Runic refuses before asking on macOS 15+.
        await MacOsMainQueue.InvokeAsync(static () =>
        {
            nint reference;
            int status;
            unsafe { status = RegisterEventHotKey(0x28, 0x0800 | 0x0200, 0x526E6963, 0xFFFFFF, GetApplicationEventTarget(), 0, &reference); }
            Console.WriteLine($"RegisterEventHotKey(Option+Shift+K) on macOS {Environment.OSVersion.Version}: OSStatus {status}.");
            if (status == 0) _ = UnregisterEventHotKey(reference);
        }).AsTask().WaitAsync(Step);

        await using (var second = MacOSPlatformProvider.CreateGlobalShortcuts())
        {
            Require(await BindAsync(second, HotKeySet(("show", HotKey))) is [{ Status: DesktopGlobalShortcutStatus.Conflict }],
                "A second registration of the key in the process conflicts.");
            Require(await BindAsync(shortcuts, HotKeySet(("other", DesktopShortcut.Parse("Primary+Control+Alt+F8")))) is [{ Status: DesktopGlobalShortcutStatus.Bound }],
                "Rebinding replaces the set.");
            Require(await BindAsync(second, HotKeySet(("show", HotKey))) is [{ Status: DesktopGlobalShortcutStatus.Bound }],
                "The key of the replaced set is free again.");
        }
        await shortcuts.DisposeAsync();
        await using (var third = MacOSPlatformProvider.CreateGlobalShortcuts())
            Require(await BindAsync(third, HotKeySet(("other", DesktopShortcut.Parse("Primary+Control+Alt+F8")))) is [{ Status: DesktopGlobalShortcutStatus.Bound }],
                "The keys of a disposed set are free again.");
        Pass("rebinding and disposal release the hot keys");
    }

    private const ulong CommandFlag = 0x100000, ControlFlag = 0x40000, OptionFlag = 0x80000;
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    private static void PostKeyPress(ushort code, ulong flags)
    {
        foreach (var down in (ReadOnlySpan<bool>)[true, false])
        {
            var key = CGEventCreateKeyboardEvent(0, code, down ? (byte)1 : (byte)0);
            if (key == 0) { Console.WriteLine("CGEventCreateKeyboardEvent failed."); return; }
            CGEventSetFlags(key, flags);
            CGEventPost(0, key);
            CFRelease(key);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct HotKeyId(uint Signature, uint Id);
    private static unsafe int RegisterEventHotKey(uint code, uint modifiers, uint signature, uint id, nint target, uint options, nint* reference) =>
        RegisterEventHotKey(code, modifiers, new HotKeyId(signature, id), target, options, reference);

    [LibraryImport(CoreGraphics)] private static partial nint CGEventCreateKeyboardEvent(nint source, ushort code, byte down);
    [LibraryImport(CoreGraphics)] private static partial void CGEventSetFlags(nint @event, ulong flags);
    [LibraryImport(CoreGraphics)] private static partial void CGEventPost(uint tap, nint @event);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static partial void CFRelease(nint value);
    [LibraryImport(Carbon)] private static partial nint GetApplicationEventTarget();
    [LibraryImport(Carbon)] private static unsafe partial int RegisterEventHotKey(uint code, uint modifiers, HotKeyId id, nint target, uint options, nint* reference);
    [LibraryImport(Carbon)] private static partial int UnregisterEventHotKey(nint reference);
}
