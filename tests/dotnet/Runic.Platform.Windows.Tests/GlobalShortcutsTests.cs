using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// RegisterHotKey on the runner's desktop session: binding, a key press through SendInput reaching Activated, a second
// registration of the same key reporting Conflict, the refused keys, and the keys released on rebind and disposal. The
// combinations use Ctrl+Alt+Shift with keys no runner tool uses, and every one is released before the case ends.
internal sealed partial class GlobalShortcutsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DesktopShortcut Key = DesktopShortcut.Parse("Primary+Alt+Shift+F9");
    private static readonly DesktopShortcut Other = DesktopShortcut.Parse("Primary+Alt+Shift+F8");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("ok: " + message);
    }

    private static ImmutableArray<DesktopGlobalShortcut> Set(params (string Id, DesktopShortcut? Shortcut)[] actions) =>
        [.. actions.Select(static action => new DesktopGlobalShortcut(action.Id, "Runic test " + action.Id) { PreferredShortcut = action.Shortcut })];

    private static ImmutableArray<DesktopGlobalShortcutBinding> Bound(PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>> result) =>
        result is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success(var bindings)
            ? bindings
            : throw new InvalidOperationException("BindAsync failed: " + result);

    [Test, Explicit, Category("NativeGlobalShortcuts")]
    [SupportedOSPlatform("windows8.0")]
    public async Task NativeGlobalShortcutsAsync()
    {
        await using var shortcuts = (WindowsGlobalShortcuts)WindowsPlatformProvider.CreateGlobalShortcuts();
        Check(DesktopServiceStatus.Get(shortcuts) is CapabilityStatus.Available, "the service is available");
        Check(await shortcuts.ListAsync() is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success({ IsEmpty: true }) && shortcuts.Window == 0,
            "listing before binding creates nothing");
        Check(await shortcuts.ConfigureAsync() is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "Windows has no settings of its own for the keys");
        var activations = Channel.CreateUnbounded<string>();
        shortcuts.Activated += (_, activation) => activations.Writer.TryWrite(activation.Id);

        var bindings = Bound(await shortcuts.BindAsync(Set(("show", Key), ("none", null), ("debugger", DesktopShortcut.Parse("Primary+F12")),
            ("copy", DesktopShortcut.Parse("Control+C")), ("twice", DesktopShortcut.Parse("Control+Alt+Shift+F9")))));
        Console.WriteLine(string.Join(Environment.NewLine, bindings));
        Check(bindings is [{ Id: "show", Status: DesktopGlobalShortcutStatus.Bound, TriggerDescription: "Ctrl+Alt+Shift+F9" },
            { Id: "none", Status: DesktopGlobalShortcutStatus.Unbound }, { Id: "debugger", Status: DesktopGlobalShortcutStatus.Refused },
            { Id: "copy", Status: DesktopGlobalShortcutStatus.Refused }, { Id: "twice", Status: DesktopGlobalShortcutStatus.Conflict }],
            "one key binds, F12 and Ctrl+C are refused, and the same key again conflicts");
        Check(shortcuts.Window != 0 && await shortcuts.ListAsync() is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success(var listed)
            && listed.SequenceEqual(bindings), "the bindings are listed");

        Press(0x78);
        using (var timeout = new CancellationTokenSource(Wait))
            Check(await activations.Reader.ReadAsync(timeout.Token) == "show", "SendInput of Ctrl+Alt+Shift+F9 activates the action");

        await using (var second = WindowsPlatformProvider.CreateGlobalShortcuts())
        {
            Check(Bound(await second.BindAsync(Set(("show", Key)))) is [{ Status: DesktopGlobalShortcutStatus.Conflict }],
                "another registration of the key conflicts");
            Check(Bound(await shortcuts.BindAsync(Set(("other", Other)))) is [{ Status: DesktopGlobalShortcutStatus.Bound }],
                "rebinding replaces the set");
            Check(Bound(await second.BindAsync(Set(("show", Key)))) is [{ Status: DesktopGlobalShortcutStatus.Bound }],
                "the key of the replaced set is free again");
        }

        Check(Bound(await shortcuts.BindAsync([])).IsEmpty, "an empty set removes every key");
        await shortcuts.DisposeAsync();
        Check(shortcuts.Window == 0, "disposal ends the window");
        await using (var third = WindowsPlatformProvider.CreateGlobalShortcuts())
            Check(Bound(await third.BindAsync(Set(("other", Other)))) is [{ Status: DesktopGlobalShortcutStatus.Bound }],
                "the keys of a disposed set are free again");
    }

    // Ctrl, Alt and Shift down, the key down and up, then the modifiers up.
    private static unsafe void Press(ushort virtualKey)
    {
        ushort[] keys = [0x11, 0x12, 0x10, virtualKey];
        var inputs = new Input[keys.Length * 2];
        for (var index = 0; index < keys.Length; index++)
        {
            inputs[index] = new Input { Type = 1, Key = new KeyboardInput { VirtualKey = keys[index] } };
            inputs[^(index + 1)] = new Input { Type = 1, Key = new KeyboardInput { VirtualKey = keys[index], Flags = 2 } };
        }
        fixed (Input* first = inputs)
        {
            var sent = SendInput((uint)inputs.Length, first, sizeof(Input));
            Check(sent == inputs.Length, $"SendInput sent {sent} of {inputs.Length} events (error {Marshal.GetLastPInvokeError()})");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public nint ExtraInfo;
    }

    // INPUT: the type, then the union, whose largest member (MOUSEINPUT) is 32 bytes on x64.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Key;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial uint SendInput(uint count, Input* inputs, int size);
}
