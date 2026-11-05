using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// The change notifications of MacDesktopSettings' watch, instead of polling:
// - NSWorkspace's notification center: NSWorkspaceAccessibilityDisplayOptionsDidChangeNotification, for Increase
//   contrast and Reduce motion;
// - the local center: NSSystemColorsDidChangeNotification, for the accent colour;
// - the distributed center: AppleInterfaceThemeChangedNotification (dark mode, also when it switches automatically) and
//   AppleColorPreferencesChangedNotification (accent and highlight colour), observed with
//   NSNotificationSuspensionBehaviorDeliverImmediately: by default AppKit holds distributed notifications while the
//   application is inactive;
// - key-value observation of NSApp's effectiveAppearance, which AppKit changes after the theme notification, so a read
//   that raced it is repeated.
// AppKit posts them on the main run loop, which Runic Desktop's event loop runs; the observation is added and removed
// there, in order. One observer object, an instance of RunicSettingsObserver mapped back to this instance, receives them
// all; each is only a wake-up for the watch's read.
internal sealed partial class MacSettingsChanges : IDisposable
{
    private const nuint DeliverImmediately = 4;
    private static readonly ConcurrentDictionary<nint, MacSettingsChanges> s_observers = new();
    private static readonly Lazy<nint> ObserverClass = new(CreateObserverClass);
    private readonly Action _changed;
    private readonly Lock _gate = new();
    private readonly List<nint> _centers = [];
    private nint _observer;

    private MacSettingsChanges(Action changed) => _changed = changed;

    internal static MacSettingsChanges Start(Action changed)
    {
        var changes = new MacSettingsChanges(changed);
        using var pool = new Pool();
        var observer = Send(Send(ObserverClass.Value, Sel("alloc")), Sel("init"));
        if (observer == 0) throw new InvalidOperationException("The settings observer could not be created.");
        lock (changes._gate)
        {
            changes._observer = observer;
            s_observers[observer] = changes;
            var selector = Sel("runicSettingsChanged:");
            var workspace = Send(Send(Class("NSWorkspace"), Sel("sharedWorkspace")), Sel("notificationCenter"));
            changes.Observe(workspace, selector, "NSWorkspaceAccessibilityDisplayOptionsDidChangeNotification");
            changes.Observe(Send(Class("NSNotificationCenter"), Sel("defaultCenter")), selector, "NSSystemColorsDidChangeNotification");
            var distributed = Send(Class("NSDistributedNotificationCenter"), Sel("defaultCenter"));
            foreach (var name in new[] { "AppleInterfaceThemeChangedNotification", "AppleColorPreferencesChangedNotification" })
                AddObserverWithBehavior(distributed, Sel("addObserver:selector:name:object:suspensionBehavior:"), observer, selector, String(name), 0,
                    DeliverImmediately);
            changes._centers.Add(distributed);
        }
        Send(observer, Sel("retain"));
        _ = MacOsMainQueue.InvokeAsync(() =>
        {
            using var pool = new Pool();
            var app = Send(Class("NSApplication"), Sel("sharedApplication"));
            Observe(app, Sel("addObserver:forKeyPath:options:context:"), observer, String("effectiveAppearance"), 0, 0);
        }).AsTask();
        return changes;
    }

    // Caller holds the gate.
    private void Observe(nint center, nint selector, string name)
    {
        Args4(center, Sel("addObserver:selector:name:object:"), _observer, selector, String(name), 0);
        _centers.Add(center);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_observer == 0) return;
            foreach (var center in _centers.Distinct()) Arg(center, Sel("removeObserver:"), _observer);
            _centers.Clear();
            s_observers.TryRemove(_observer, out _);
            Release(_observer);
            // The observation's reference, released once it is removed on the main queue after it was added there.
            var observer = _observer;
            _observer = 0;
            _ = MacOsMainQueue.InvokeAsync(() =>
            {
                using var pool = new Pool();
                var app = Send(Class("NSApplication"), Sel("sharedApplication"));
                Args(app, Sel("removeObserver:forKeyPath:"), observer, String("effectiveAppearance"));
                Release(observer);
            }).AsTask();
        }
    }

    private static unsafe nint CreateObserverClass()
    {
        var type = AllocateClass(Class("NSObject"), "RunicSettingsObserver", 0);
        if (type == 0) throw new InvalidOperationException("The settings observer class already exists.");
        AddMethod(type, Sel("runicSettingsChanged:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Changed, "v@:@");
        AddMethod(type, Sel("observeValueForKeyPath:ofObject:change:context:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&ValueChanged, "v@:@@@^v");
        RegisterClass(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Changed(nint self, nint selector, nint notification)
    {
        try { if (s_observers.TryGetValue(self, out var changes)) changes._changed(); }
        catch { /* Never unwind through AppKit. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ValueChanged(nint self, nint selector, nint keyPath, nint observed, nint change, nint context)
    {
        try { if (s_observers.TryGetValue(self, out var changes)) changes._changed(); }
        catch { /* Never unwind through AppKit. */ }
    }

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void Observe(nint target, nint selector, nint observer, nint keyPath, nuint options, nint context);
    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint AllocateClass(nint parent, string name, nuint extra);
    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")] private static partial void RegisterClass(nint type);
    [LibraryImport(ObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    private static partial byte AddMethod(nint type, nint selector, nint implementation, string types);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void AddObserverWithBehavior(nint target, nint selector, nint observer, nint observed, nint name, nint @object, nuint behavior);
}
