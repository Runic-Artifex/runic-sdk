using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Runic.Platform.MacOS;

// Global shortcuts through the Carbon hot keys (RegisterEventHotKey), which need no Accessibility permission and consume
// the key, unlike a global NSEvent monitor. One kEventHotKeyPressed handler on the application event target serves every
// instance; the hot key's ID carries the instance and the action. Carbon events reach it while the Desktop event loop
// runs NSApplication, and every Carbon call runs on the main thread: without that loop, requests report BackendUnavailable
// after MainThreadTimeout. Letters and punctuation are the keys that type them on the current ASCII-capable layout, so a
// change of input source registers the keys again and reports the bindings that changed.
internal sealed partial class MacGlobalShortcuts : IDesktopGlobalShortcuts, IDesktopServiceStatus
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    // 'Rnic', the hot key ID signature; 'keyb', kEventHotKeyPressed; '----' and 'hkid' for the event's EventHotKeyID.
    private const uint Signature = 0x526E6963, KeyboardClass = 0x6B657962, HotKeyPressed = 5;
    private const uint DirectObject = 0x2D2D2D2D, HotKeyIdType = 0x686B6964;
    // eventHotKeyExistsErr: another application or hot key has the combination. eventNotHandledErr passes the event on.
    internal const int HotKeyExists = -9878;
    private const int NotHandled = -9874;
    private const uint DisplayAction = 3, NoDeadKeys = 1;
    private static readonly ConcurrentDictionary<uint, MacGlobalShortcuts> Instances = new();
    private static uint s_nextInstance;
    // Main thread only.
    private static bool s_handlerInstalled, s_observing;

    private readonly uint _instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<object> _events = Channel.CreateUnbounded<object>(new() { SingleReader = true });
    private Task? _pump;
    private volatile bool _queued, _disposed;
    // Main thread only: the set, its registered hot keys and the bindings last reported.
    private ImmutableArray<DesktopGlobalShortcut> _set = [];
    private readonly List<nint> _hotKeys = [];
    private ImmutableArray<DesktopGlobalShortcutBinding> _bindings = [];

    internal MacGlobalShortcuts()
    {
        _instance = Interlocked.Increment(ref s_nextInstance);
        Instances[_instance] = this;
    }

    /// <summary>How long a request waits for the main thread. Only the Desktop event loop services it.</summary>
    internal static TimeSpan MainThreadTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public CapabilityStatus Status { get; } = new CapabilityStatus.Available();

    public event EventHandler<DesktopGlobalShortcutActivation>? Activated;
    public event EventHandler<DesktopGlobalShortcutsChange>? BindingsChanged;

    public async ValueTask<PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>> BindAsync(
        ImmutableArray<DesktopGlobalShortcut> shortcuts, CancellationToken cancellationToken = default)
    {
        DesktopGlobalShortcutRules.Validate(shortcuts);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pump ??= Task.Run(PumpAsync, CancellationToken.None);
            PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>> result =
                new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            _queued = true;
            // A bind the caller gave up on does not run later, so it never undoes a later one.
            await OnMainThreadAsync(() =>
            {
                if (_disposed || !MacAppKit.IsAvailable) return;
                MacAppKit.EnsureApplication();
                if (!InstallHandler()) return;
                _set = shortcuts;
                _bindings = Register();
                result = new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success(_bindings);
            }, abandonable: true, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_queued) return new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success([]);
            ImmutableArray<DesktopGlobalShortcutBinding> bindings = [];
            return await OnMainThreadAsync(() => bindings = _bindings, abandonable: true, cancellationToken).ConfigureAwait(false)
                ? new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success(bindings)
                : new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<PlatformResult<PlatformUnit>> ConfigureAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PlatformResult<PlatformUnit>>(
            new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
    }

    public async ValueTask DisposeAsync()
    {
        bool entered = await _gate.WaitAsync(MainThreadTimeout + MainThreadTimeout).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            Instances.TryRemove(_instance, out _);
            _events.Writer.TryComplete();
            // Without an event loop the hot keys end with the process.
            if (_queued) await OnMainThreadAsync(() => { Unregister(); _set = []; }, abandonable: false, CancellationToken.None).ConfigureAwait(false);
        }
        finally { if (entered) _gate.Release(); }
    }

    // The key code and Carbon modifiers of a shortcut, or null when macOS cannot register it: a standard menu's shortcut,
    // a key without a Mac key code, a character the layout lacks, and Option or Option+Shift alone, which macOS 15 and
    // later refuse so that hot keys cannot read the characters Option types.
    internal static (uint Code, uint Modifiers)? Map(DesktopShortcut shortcut, Func<char, (ushort Code, bool Shift)?> layout, bool refusesOption)
    {
        if (DesktopShortcutKeys.ReservedBy(shortcut, macOS: true) is not null) return null;
        var modifiers = DesktopShortcutKeys.CarbonModifiers(shortcut.Modifiers);
        uint code;
        if (DesktopShortcutKeys.IsMacLayoutIndependent(shortcut.Key))
        {
            if (DesktopShortcutKeys.MacKeyCode(shortcut.Key) is not { } fixedCode) return null;
            code = fixedCode;
        }
        else
        {
            var character = DesktopShortcutKeys.Character(shortcut.Key) ?? char.ToLowerInvariant(shortcut.Key[0]);
            if (layout(character) is { } found)
            {
                // A letter is its unshifted key; Shift stays as declared.
                if (found.Shift && char.IsAsciiLetter(character)) return null;
                code = found.Code;
                if (found.Shift) modifiers |= DesktopShortcutKeys.CarbonShift;
            }
            else if (char.IsAsciiLetter(character) && DesktopShortcutKeys.MacKeyCode(shortcut.Key) is { } ansi) code = ansi;
            else return null;
        }
        if (refusesOption && (modifiers & DesktopShortcutKeys.CarbonOption) != 0
            && (modifiers & (DesktopShortcutKeys.CarbonCommand | DesktopShortcutKeys.CarbonControl)) == 0)
            return null;
        return (code, modifiers);
    }

    // On the main thread: the previous keys go, then each key of the set is registered on its own.
    private unsafe ImmutableArray<DesktopGlobalShortcutBinding> Register()
    {
        Unregister();
        var refusesOption = OperatingSystem.IsMacOSVersionAtLeast(15);
        var layout = new KeyboardLayout();
        try
        {
            var bindings = ImmutableArray.CreateBuilder<DesktopGlobalShortcutBinding>(_set.Length);
            for (var index = 0; index < _set.Length; index++)
            {
                var action = _set[index];
                var requested = DesktopGlobalShortcutRules.Requested(action);
                var status = DesktopGlobalShortcutStatus.Unbound;
                if (requested is { } shortcut)
                {
                    status = DesktopGlobalShortcutStatus.Refused;
                    if (Map(shortcut, layout.Find, refusesOption) is { } key)
                    {
                        var id = new HotKeyId(Signature, (_instance << 8) | (uint)(index + 1));
                        nint hotKey;
                        var error = RegisterEventHotKey(key.Code, key.Modifiers, id, GetApplicationEventTarget(), 0, &hotKey);
                        if (error == 0) { _hotKeys.Add(hotKey); status = DesktopGlobalShortcutStatus.Bound; }
                        else if (error == HotKeyExists) status = DesktopGlobalShortcutStatus.Conflict;
                    }
                }
                bindings.Add(DesktopGlobalShortcutRules.Binding(action.Id, requested, status, macOS: true));
            }
            return bindings.MoveToImmutable();
        }
        finally { layout.Dispose(); }
    }

    private void Unregister()
    {
        foreach (var hotKey in _hotKeys) _ = UnregisterEventHotKey(hotKey);
        _hotKeys.Clear();
    }

    // After the input source changed: the keys that type the set's characters may have moved.
    private void LayoutChanged()
    {
        if (_disposed || _set.IsEmpty) return;
        var bindings = Register();
        if (bindings.SequenceEqual(_bindings)) return;
        _bindings = bindings;
        _events.Writer.TryWrite(new DesktopGlobalShortcutsChange(bindings));
    }

    private static unsafe bool InstallHandler()
    {
        if (!s_handlerInstalled)
        {
            var type = new EventTypeSpec(KeyboardClass, HotKeyPressed);
            nint handler;
            if (InstallEventHandler(GetApplicationEventTarget(), &OnHotKey, 1, &type, 0, &handler) != 0) return false;
            s_handlerInstalled = true;
        }
        if (!s_observing && SelectedInputSourceChanged != 0)
        {
            CFNotificationCenterAddObserver(CFNotificationCenterGetDistributedCenter(), 0, &OnInputSourceChanged,
                SelectedInputSourceChanged, 0, DeliverImmediately);
            s_observing = true;
        }
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnHotKey(nint call, nint @event, nint data)
    {
        try
        {
            HotKeyId id;
            if (GetEventParameter(@event, DirectObject, HotKeyIdType, null, (nuint)sizeof(HotKeyId), null, &id) != 0
                || id.Signature != Signature || !Instances.TryGetValue(id.Id >> 8, out var owner))
                return NotHandled;
            var index = (int)(id.Id & 0xFF) - 1;
            var set = owner._set;
            if (index >= 0 && index < set.Length && !owner._disposed)
                owner._events.Writer.TryWrite(new DesktopGlobalShortcutActivation(set[index].Id));
            return 0;
        }
        catch { return NotHandled; /* Never unwind through Carbon. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnInputSourceChanged(nint center, nint observer, nint name, nint @object, nint userInfo)
    {
        foreach (var owner in Instances.Values)
        {
            try { owner.LayoutChanged(); }
            catch { /* Never unwind through Core Foundation. */ }
        }
    }

    private async Task PumpAsync()
    {
        await foreach (var item in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_disposed) return;
            try
            {
                if (item is DesktopGlobalShortcutActivation activation) Activated?.Invoke(this, activation);
                else if (item is DesktopGlobalShortcutsChange change) BindingsChanged?.Invoke(this, change);
            }
            catch (Exception error)
            {
                // As with an event raised on the thread pool, a throwing handler fails the process; later events still arrive until then.
                var capture = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                ThreadPool.QueueUserWorkItem(static state => ((System.Runtime.ExceptionServices.ExceptionDispatchInfo)state!).Throw(), capture);
            }
        }
    }

    // Runs work on the main thread and reports whether it ran within MainThreadTimeout. Abandonable work that has not
    // started when the wait ends never runs; other work stays queued.
    private static async Task<bool> OnMainThreadAsync(Action work, bool abandonable, CancellationToken cancellationToken)
    {
        var state = new StrongBox<int>(); // 0 queued, 1 started, 2 abandoned
        var queued = MacOsMainQueue.InvokeAsync(() =>
        {
            if (abandonable && Interlocked.CompareExchange(ref state.Value, 1, 0) != 0) return;
            work();
        }).AsTask();
        try
        {
            await queued.WaitAsync(MainThreadTimeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            if (abandonable && Interlocked.CompareExchange(ref state.Value, 2, 0) == 1)
            {
                await queued.ConfigureAwait(false);
                return true;
            }
            if (error is OperationCanceledException) throw;
            return false;
        }
    }

    // The current ASCII-capable keyboard layout, searched for the key that types a character. Main thread only.
    private sealed unsafe class KeyboardLayout : IDisposable
    {
        private readonly nint _source;
        private readonly nint _layout;
        private readonly uint _keyboardType;

        internal KeyboardLayout()
        {
            _source = TISCopyCurrentASCIICapableKeyboardLayoutInputSource();
            if (_source == 0 || UnicodeKeyLayoutData == 0) return;
            var data = TISGetInputSourceProperty(_source, UnicodeKeyLayoutData);
            if (data != 0) _layout = CFDataGetBytePtr(data);
            _keyboardType = LMGetKbdType();
        }

        // The key that types the character without modifiers, or else with Shift.
        internal (ushort Code, bool Shift)? Find(char character)
        {
            if (_layout == 0) return null;
            foreach (var shift in (ReadOnlySpan<bool>)[false, true])
            {
                for (ushort code = 0; code < 128; code++)
                {
                    uint deadKeys = 0;
                    nuint length = 0;
                    char typed = '\0';
                    var modifiers = shift ? (DesktopShortcutKeys.CarbonShift >> 8) & 0xFF : 0;
                    if (UCKeyTranslate(_layout, code, (ushort)DisplayAction, modifiers, _keyboardType, NoDeadKeys, &deadKeys, 1, &length, &typed) == 0
                        && length == 1 && typed == character)
                        return (code, shift);
                }
            }
            return null;
        }

        public void Dispose() { if (_source != 0) CFRelease(_source); }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct HotKeyId(uint Signature, uint Id);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct EventTypeSpec(uint EventClass, uint EventKind);

    private const nint DeliverImmediately = 4;
    private static readonly nint CarbonLibrary = NativeLibrary.TryLoad(Carbon, out var handle) ? handle : 0;
    private static readonly nint UnicodeKeyLayoutData = Constant("kTISPropertyUnicodeKeyLayoutData");
    private static readonly nint SelectedInputSourceChanged = Constant("kTISNotifySelectedKeyboardInputSourceChanged");

    // A CFStringRef constant that Carbon exports.
    private static nint Constant(string name) =>
        CarbonLibrary != 0 && NativeLibrary.TryGetExport(CarbonLibrary, name, out var address) ? Marshal.ReadIntPtr(address) : 0;

    [LibraryImport(Carbon)] private static partial nint GetApplicationEventTarget();
    [LibraryImport(Carbon)] private static unsafe partial int RegisterEventHotKey(uint code, uint modifiers, HotKeyId id, nint target, uint options, nint* hotKey);
    [LibraryImport(Carbon)] private static partial int UnregisterEventHotKey(nint hotKey);
    [LibraryImport(Carbon)] private static unsafe partial int InstallEventHandler(nint target, delegate* unmanaged[Cdecl]<nint, nint, nint, int> handler,
        nuint count, EventTypeSpec* types, nint data, nint* handlerReference);
    [LibraryImport(Carbon)] private static unsafe partial int GetEventParameter(nint @event, uint name, uint type, uint* actualType, nuint size,
        nuint* actualSize, void* data);
    [LibraryImport(Carbon)] private static partial nint TISCopyCurrentASCIICapableKeyboardLayoutInputSource();
    [LibraryImport(Carbon)] private static partial nint TISGetInputSourceProperty(nint source, nint key);
    [LibraryImport(Carbon)] private static partial byte LMGetKbdType();
    [LibraryImport(Carbon)] private static unsafe partial int UCKeyTranslate(nint layout, ushort code, ushort action, uint modifiers, uint keyboardType,
        uint options, uint* deadKeyState, nuint maximumLength, nuint* length, char* text);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataGetBytePtr(nint data);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(nint value);
    [LibraryImport(CoreFoundation)] private static partial nint CFNotificationCenterGetDistributedCenter();
    [LibraryImport(CoreFoundation)] private static unsafe partial void CFNotificationCenterAddObserver(nint center, nint observer,
        delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void> callback, nint name, nint @object, nint behavior);
}
