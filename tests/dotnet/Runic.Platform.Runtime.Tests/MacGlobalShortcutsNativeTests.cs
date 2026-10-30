using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Platform;
using Runic.Platform.MacOS;


// Carbon hot keys inside the Desktop event loop: binding, the refused keys, a second registration of the same key in the
// process, and release on rebind and disposal. macOS's own answer to an Option+Shift-only hot key is reported, not
// required. Key presses are not posted: without Accessibility trust CGEventPost does not reach the hot key on the runner
// (observed on macOS 26), and posted events disturb the status item cases that run next.
internal sealed partial class MacAppKitNativeTests
{
    private static readonly DesktopShortcut HotKey = DesktopShortcut.Parse("Primary+Control+Alt+F9");

    private static ImmutableArray<DesktopGlobalShortcut> HotKeySet(params (string Id, DesktopShortcut? Shortcut)[] actions) =>
        [.. actions.Select(static action => new DesktopGlobalShortcut(action.Id, "Runic test " + action.Id) { PreferredShortcut = action.Shortcut })];

    private static async Task<ImmutableArray<DesktopGlobalShortcutBinding>> BindAsync(IDesktopGlobalShortcuts shortcuts,
        ImmutableArray<DesktopGlobalShortcut> set) =>
        await shortcuts.BindAsync(set).AsTask().WaitScaledAsync(Step) is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success(var bindings)
            ? bindings
            : throw new InvalidOperationException("BindAsync did not succeed.");

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static async Task GlobalShortcutsAsync()
    {
        await using var shortcuts = MacOSPlatformProvider.CreateGlobalShortcuts();
        Require(await shortcuts.ConfigureAsync() is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "ConfigureAsync on macOS.");
        var bindings = await BindAsync(shortcuts, HotKeySet(("show", HotKey), ("none", null), ("quit", DesktopShortcut.Parse("Primary+Q")),
            ("option", DesktopShortcut.Parse("Alt+Shift+K")), ("twice", HotKey)));
        Console.WriteLine(string.Join(Environment.NewLine, bindings));
        Require(bindings is [{ Id: "show", Status: DesktopGlobalShortcutStatus.Bound, TriggerDescription: "⌃⌥⌘F9" },
            { Status: DesktopGlobalShortcutStatus.Unbound }, { Status: DesktopGlobalShortcutStatus.Refused }, var option,
            { Id: "twice", Status: DesktopGlobalShortcutStatus.Conflict }]
            && option.Status is DesktopGlobalShortcutStatus.Bound or DesktopGlobalShortcutStatus.Refused,
            "Bindings: one key binds, ⌘Q is refused, Option+Shift alone is macOS's answer and the same key again conflicts.");
        Require(await shortcuts.ListAsync() is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success(var listed)
            && listed.SequenceEqual(bindings), "ListAsync reports the bindings.");
        Pass("Carbon hot keys bind, refuse the standard menus' keys and report a repeated key as a conflict");

        // Finding: macOS's own answer to an Option+Shift-only hot key, which Runic passes on (OSStatus 0 on macOS 26.6).
        await MacOsMainQueue.InvokeAsync(static () =>
        {
            nint reference;
            int status;
            unsafe { status = RegisterEventHotKey(0x28, 0x0800 | 0x0200, 0x526E6963, 0xFFFFFF, GetApplicationEventTarget(), 0, &reference); }
            Console.WriteLine($"RegisterEventHotKey(Option+Shift+K) on macOS {Environment.OSVersion.Version}: OSStatus {status}.");
            if (status == 0) _ = UnregisterEventHotKey(reference);
        }).AsTask().WaitScaledAsync(Step);

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

    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    [StructLayout(LayoutKind.Sequential)] private readonly record struct HotKeyId(uint Signature, uint Id);
    private static unsafe int RegisterEventHotKey(uint code, uint modifiers, uint signature, uint id, nint target, uint options, nint* reference) =>
        RegisterEventHotKey(code, modifiers, new HotKeyId(signature, id), target, options, reference);

    [LibraryImport(Carbon)] private static partial nint GetApplicationEventTarget();
    [LibraryImport(Carbon)] private static unsafe partial int RegisterEventHotKey(uint code, uint modifiers, HotKeyId id, nint target, uint options, nint* reference);
    [LibraryImport(Carbon)] private static partial int UnregisterEventHotKey(nint reference);
}
