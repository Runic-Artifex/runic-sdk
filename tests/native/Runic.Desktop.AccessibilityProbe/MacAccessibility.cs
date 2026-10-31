using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// The macOS accessibility API, which VoiceOver uses: AXUIElement for the fixture's application, its window, menu bar and
// the WKWebView's web area, and an AXObserver for its notifications. Requires the Accessibility permission (TCC) for the
// process responsible for the probe; GitHub's macOS runners grant it to the runner's agent.
[SupportedOSPlatform("macos")]
internal sealed partial class MacAccessibility : AccessibilityClient
{
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int NumberDouble = 13; // kCFNumberDoubleType
    private const int CannotComplete = -25204; // kAXErrorCannotComplete
    private const int AlreadyRegistered = -25209; // kAXErrorNotificationAlreadyRegistered
    private static MacAccessibility? s_instance;
    private static readonly Dictionary<string, nint> s_strings = [];
    private readonly nint _application;
    private readonly int _pid;
    private readonly Thread _loop;
    private readonly TaskCompletionSource _observing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _disposed;

    // The application answers the accessibility API once AppKit runs its event loop, and the page's controls appear once
    // WKWebView has loaded it; both are condition waits, scaled on a slow runner.
    private static readonly TimeSpan EventLoopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(10);

    private MacAccessibility(int pid)
    {
        _pid = pid;
        _application = AXUIElementCreateApplication(pid);
        _loop = new Thread(Observe) { IsBackground = true, Name = "AX observer" };
    }

    internal override string Api => "macOS accessibility (AXUIElement)";

    internal static async Task<MacAccessibility> CreateAsync(int pid, Action listening, CancellationToken cancellationToken)
    {
        listening();
        if (!AXIsProcessTrusted())
            throw new InvalidOperationException("The probe has no Accessibility permission (AXIsProcessTrusted is false).");
        var client = new MacAccessibility(pid);
        s_instance = client;
        client._loop.Start();
        await client._observing.Task.WaitScaledAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await Task.Run(() => client.Observe(pid, client._application), cancellationToken);
        return client;
    }

    private static readonly string[] Notifications =
    [
        "AXValueChanged", "AXSelectedChildrenChanged", "AXSelectedRowsChanged", "AXAnnouncementRequested",
        "AXFocusedUIElementChanged", "AXLiveRegionChanged", "AXLiveRegionCreated",
    ];
    private nint _runLoop;

    // The observer thread's run loop delivers the notifications.
    private void Observe()
    {
        try
        {
            _runLoop = CFRetain(CFRunLoopGetCurrent());
            _observing.TrySetResult();
            while (!_disposed) _ = CFRunLoopRunInMode(DefaultMode, 0.25, 0);
        }
        catch (Exception error)
        {
            _observing.TrySetException(error);
        }
    }

    private static unsafe nint True => *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFBooleanTrue");

    private static unsafe nint DefaultMode => *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFRunLoopDefaultMode");

    // Observes the application element. WebKit's notifications for the page reach VoiceOver but no observer of this
    // public API, here or on the web area (Expectations.cs); the window's own, such as announcements, do.
    private unsafe void Observe(int pid, nint application)
    {
        nint observer;
        Check(AXObserverCreateWithInfoCallback(pid, &OnNotification, &observer), "AXObserverCreateWithInfoCallback");
        // The application answers once AppKit runs its event loop (kAXErrorCannotComplete before).
        var wait = TestTimeouts.Start(EventLoopTimeout);
        foreach (var name in Notifications)
        {
            var error = AXObserverAddNotification(observer, application, String(name), 0);
            while (error == CannotComplete && !wait.IsExpired)
            {
                Thread.Sleep(100);
                error = AXObserverAddNotification(observer, application, String(name), 0);
            }
            if (error != AlreadyRegistered) Check(error, $"AXObserverAddNotification {name}");
        }
        wait.Succeeded();
        CFRunLoopAddSource(_runLoop, AXObserverGetRunLoopSource(observer), DefaultMode);
        CFRunLoopWakeUp(_runLoop);
    }

    [UnmanagedCallersOnly]
    private unsafe static void OnNotification(nint observer, nint element, nint notification, nint info, nint context)
    {
        try { s_instance?.Notify(element, ReadString(notification) ?? "", info); }
        catch (Exception error) { Console.Error.WriteLine("AX notification failed: " + error.Message); }
    }

    private void Notify(nint element, string notification, nint info)
    {
        EventKind? kind = notification switch
        {
            "AXValueChanged" => EventKind.ValueChanged,
            "AXSelectedChildrenChanged" or "AXSelectedRowsChanged" => EventKind.SelectionChanged,
            "AXAnnouncementRequested" => EventKind.Announcement,
            "AXLiveRegionChanged" or "AXLiveRegionCreated" => EventKind.LiveRegion,
            "AXFocusedUIElementChanged" => EventKind.FocusChanged,
            _ => null,
        };
        if (kind is null) return;
        var role = Text(element, "AXRole") ?? "";
        var detail = kind switch
        {
            EventKind.ValueChanged => Text(element, "AXValue") ?? "",
            EventKind.SelectionChanged => string.Join(", ", Selection(element)),
            EventKind.Announcement => info != 0 && CFGetTypeID(info) == CFDictionaryGetTypeID()
                ? Describe(CFDictionaryGetValue(info, String("AXAnnouncementKey"))) ?? "" : "",
            EventKind.LiveRegion => Contents(element, 0),
            _ => "",
        };
        Raise(new AccessibilityEvent(kind.Value, Name(element), role, $"{notification} {detail}".Trim()));
    }

    internal override Task<string> WindowTitleAsync(CancellationToken cancellationToken)
    {
        var windows = Children(_application, "AXWindows");
        return Task.FromResult(windows.Count == 0 ? "" : $"{Text(windows[0], "AXTitle")} ({Text(windows[0], "AXRole")})");
    }

    internal override Task<string?> MenuBarAsync(CancellationToken cancellationToken)
    {
        var bar = Copy(_application, "AXMenuBar");
        if (bar == 0) return Task.FromResult<string?>(null);
        var items = Children(bar, "AXChildren").Select(item => Text(item, "AXTitle") ?? "?");
        return Task.FromResult<string?>("menu bar: " + string.Join(", ", items));
    }

    internal override async Task<Control?> FindAsync(ControlKind kind, string name, CancellationToken cancellationToken)
    {
        string[] roles = kind switch
        {
            ControlKind.Button => ["AXButton"],
            ControlKind.Slider => ["AXSlider"],
            // An ARIA spin button is an incrementor; <input type="number"> a text field with an incrementor.
            ControlKind.SpinButton => ["AXIncrementor", "AXTextField"],
            ControlKind.ListBox => ["AXList", "AXListBox"],
            _ => [],
        };
        // The page loads after the window shows.
        var wait = TestTimeouts.Start(FindTimeout);
        while (true)
        {
            var matches = new List<Control>();
            Walk(_application, 0, element =>
            {
                var role = Text(element, "AXRole");
                if (role is not null && roles.Contains(role) && Name(element) == name)
                    matches.Add(new Control(kind, name, $"{role} ({Text(element, "AXRoleDescription")})", element));
            });
            if (matches.Count == 1)
            {
                wait.Succeeded();
                return matches[0];
            }
            if (matches.Count > 1) throw new InvalidOperationException($"{matches.Count} elements match {kind} '{name}'.");
            if (wait.IsExpired) return null;
            await Task.Delay(200, cancellationToken);
        }
    }

    private static void Walk(nint element, int depth, Action<nint> visit)
    {
        if (depth > 40) return;
        visit(element);
        foreach (var child in Children(element, "AXChildren")) Walk(child, depth + 1, visit);
    }

    internal override Task<RangeState> ReadRangeAsync(Control control, CancellationToken cancellationToken)
    {
        var element = (nint)control.Handle;
        return Task.FromResult(new RangeState(Number(element, "AXValue"), Number(element, "AXMinValue"), Number(element, "AXMaxValue")));
    }

    // VoiceOver changes a slider with its increment and decrement commands (AXIncrement, AXDecrement).
    internal override async Task ChangeRangeAsync(Control control, double value, CancellationToken cancellationToken)
    {
        var element = (nint)control.Handle;
        for (var step = 0; step < 50 && Number(element, "AXValue") is { } now && now != value; step++)
        {
            Check(AXUIElementPerformAction(element, String(now < value ? "AXIncrement" : "AXDecrement")), "AXIncrement");
            await Task.Delay(50, cancellationToken);
        }
    }

    internal override Task StepAsync(Control control, int direction, CancellationToken cancellationToken)
    {
        Check(AXUIElementPerformAction((nint)control.Handle, String(direction > 0 ? "AXIncrement" : "AXDecrement")), "AXIncrement");
        return Task.CompletedTask;
    }

    internal override Task PressAsync(Control control, CancellationToken cancellationToken)
    {
        Check(AXUIElementPerformAction((nint)control.Handle, String("AXPress")), "AXPress " + control.Name);
        return Task.CompletedTask;
    }

    internal override Task<IReadOnlyList<string>> ReadSelectionAsync(Control control, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>(Selection((nint)control.Handle));

    internal override unsafe Task SelectAsync(Control control, string item, CancellationToken cancellationToken)
    {
        var list = (nint)control.Handle;
        var child = Children(list, "AXChildren").FirstOrDefault(child => Name(child) == item);
        if (child == 0) throw new InvalidOperationException($"'{control.Name}' has no item '{item}'.");
        var array = CFArrayCreate(0, &child, 1, NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFTypeArrayCallBacks"));
        Check(AXUIElementSetAttributeValue(list, String("AXSelectedChildren"), array), "AXSelectedChildren");
        CFRelease(array);
        return Task.CompletedTask;
    }

    internal override Task FocusAsync(Control control, CancellationToken cancellationToken)
    {
        Check(AXUIElementSetAttributeValue((nint)control.Handle, String("AXFocused"), True), "AXFocused");
        return Task.CompletedTask;
    }

    private static List<string> Selection(nint list)
    {
        var selected = Children(list, "AXSelectedChildren");
        if (selected.Count == 0) selected = Children(list, "AXChildren").Where(child => Number(child, "AXSelected") == 1).ToList();
        return selected.Select(Name).ToList();
    }

    // The accessible name as VoiceOver reads it: the title, the description, or a text's value.
    private static string Name(nint element)
    {
        foreach (var attribute in new[] { "AXTitle", "AXDescription" })
        {
            if (Text(element, attribute) is { Length: > 0 } text) return text;
        }
        var role = Text(element, "AXRole");
        if (role is "AXStaticText" or "AXRow" or "AXCell") return Text(element, "AXValue") ?? Contents(element, 0);
        // An option in an ARIA list box names itself through its text.
        return Text(element, "AXRoleDescription") is "option" or "list item" ? Contents(element, 0) : "";
    }

    private static string Contents(nint element, int depth)
    {
        if (depth > 6) return "";
        if (Text(element, "AXRole") == "AXStaticText") return Text(element, "AXValue") ?? "";
        var title = depth == 0 ? Text(element, "AXTitle") : null;
        if (title is { Length: > 0 }) return title;
        return string.Join(" ", Children(element, "AXChildren").Select(child => Contents(child, depth + 1)).Where(text => text.Length > 0));
    }

    private static List<nint> Children(nint element, string attribute)
    {
        var value = Copy(element, attribute);
        var children = new List<nint>();
        if (value == 0 || CFGetTypeID(value) != CFArrayGetTypeID()) return children;
        var count = CFArrayGetCount(value);
        for (nint index = 0; index < count; index++) children.Add(CFRetain(CFArrayGetValueAtIndex(value, index)));
        CFRelease(value);
        return children;
    }

    private unsafe static nint Copy(nint element, string attribute)
    {
        nint value;
        return AXUIElementCopyAttributeValue(element, String(attribute), &value) == 0 ? value : 0;
    }

    private static string? Text(nint element, string attribute)
    {
        var value = Copy(element, attribute);
        if (value == 0) return null;
        try { return Describe(value); }
        finally { CFRelease(value); }
    }

    private unsafe static double? Number(nint element, string attribute)
    {
        var value = Copy(element, attribute);
        if (value == 0) return null;
        try
        {
            var type = CFGetTypeID(value);
            if (type == CFNumberGetTypeID())
            {
                double number;
                return CFNumberGetValue(value, NumberDouble, &number) != 0 ? number : null;
            }
            if (type == CFBooleanGetTypeID()) return CFBooleanGetValue(value) != 0 ? 1 : 0;
            return double.TryParse(ReadString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
        finally { CFRelease(value); }
    }

    private unsafe static string? Describe(nint value)
    {
        if (value == 0) return null;
        var type = CFGetTypeID(value);
        if (type == CFStringGetTypeID()) return ReadString(value);
        if (type == CFNumberGetTypeID())
        {
            double number;
            return CFNumberGetValue(value, NumberDouble, &number) != 0 ? number.ToString(CultureInfo.InvariantCulture) : null;
        }
        if (type == CFBooleanGetTypeID()) return CFBooleanGetValue(value) != 0 ? "true" : "false";
        return null;
    }

    private unsafe static string? ReadString(nint value)
    {
        if (value == 0 || CFGetTypeID(value) != CFStringGetTypeID()) return null;
        var length = CFStringGetLength(value);
        var buffer = new char[length];
        fixed (char* characters = buffer) CFStringGetCharacters(value, new CFRange(0, length), characters);
        return new string(buffer);
    }

    private unsafe static nint String(string value)
    {
        lock (s_strings)
        {
            if (s_strings.TryGetValue(value, out var existing)) return existing;
            fixed (char* characters = value)
            {
                var created = CFStringCreateWithCharacters(0, characters, value.Length);
                s_strings[value] = created;
                return created;
            }
        }
    }

    private static void Check(int error, string call)
    {
        if (error != 0) throw new InvalidOperationException($"{call} failed with AXError {error}.");
    }

    public override ValueTask DisposeAsync()
    {
        _disposed = true;
        _loop.Join(TestTimeouts.Scale(TimeSpan.FromSeconds(2)));
        return ValueTask.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CFRange(nint Location, nint Length);

    [LibraryImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool AXIsProcessTrusted();

    [LibraryImport(ApplicationServices)]
    private static partial nint AXUIElementCreateApplication(int pid);


    [LibraryImport(ApplicationServices)]
    private static unsafe partial int AXUIElementCopyAttributeValue(nint element, nint attribute, nint* value);

    [LibraryImport(ApplicationServices)]
    private static partial int AXUIElementSetAttributeValue(nint element, nint attribute, nint value);

    [LibraryImport(ApplicationServices)]
    private static partial int AXUIElementPerformAction(nint element, nint action);

    [LibraryImport(ApplicationServices)]
    private static unsafe partial int AXObserverCreateWithInfoCallback(int pid, delegate* unmanaged<nint, nint, nint, nint, nint, void> callback, nint* observer);

    [LibraryImport(ApplicationServices)]
    private static partial int AXObserverAddNotification(nint observer, nint element, nint notification, nint context);

    [LibraryImport(ApplicationServices)]
    private static partial nint AXObserverGetRunLoopSource(nint observer);

    [LibraryImport(CoreFoundation)]
    private static unsafe partial nint CFStringCreateWithCharacters(nint allocator, char* characters, nint length);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(nint value);

    [LibraryImport(CoreFoundation)]
    private static unsafe partial void CFStringGetCharacters(nint value, CFRange range, char* buffer);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFGetTypeID(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFStringGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFNumberGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFBooleanGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFArrayGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFDictionaryGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static unsafe partial byte CFNumberGetValue(nint number, int type, double* value);

    [LibraryImport(CoreFoundation)]
    private static partial byte CFBooleanGetValue(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(nint array);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    [LibraryImport(CoreFoundation)]
    private static unsafe partial nint CFArrayCreate(nint allocator, nint* values, nint count, nint callbacks);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDictionaryGetValue(nint dictionary, nint key);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFRetain(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFRunLoopGetCurrent();

    [LibraryImport(CoreFoundation)]
    private static partial void CFRunLoopAddSource(nint loop, nint source, nint mode);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRunLoopWakeUp(nint loop);

    [LibraryImport(CoreFoundation)]
    private static partial int CFRunLoopRunInMode(nint mode, double seconds, byte returnAfterSourceHandled);
}
