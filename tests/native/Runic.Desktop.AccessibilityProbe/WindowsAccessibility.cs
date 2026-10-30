using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;

// UI Automation, which Narrator and NVDA use: the fixture's top-level window by process ID, WebView2's provider tree
// below it, the RangeValue, Selection, SelectionItem and Invoke patterns, and the property, selection, live region,
// notification and focus events.
internal sealed partial class WindowsAccessibility : AccessibilityClient
{
    private readonly AutomationElement _window;
    private readonly List<(AutomationEvent Event, AutomationEventHandler Handler)> _handlers = [];
    private readonly AutomationPropertyChangedEventHandler _valueChanged;
    private readonly AutomationFocusChangedEventHandler _focusChanged;

    // The fixture starts the probe before it opens its window, and the WebView2 host shows its window only once WebView2
    // has started (WindowsWebView2Host.InitializeAsync); UI Automation lists shown top-level windows only. That usually
    // takes a few seconds, but the first WebView2 start on a fresh runner image has taken over a minute.
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<WindowsAccessibility> CreateAsync(int pid, Action listening, CancellationToken cancellationToken)
    {
        listening();
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
        AutomationElement? window = null;
        if (!await TestTimeouts.UntilAsync(() => (window = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition)) is not null,
                WindowTimeout, TimeSpan.FromMilliseconds(100), cancellationToken))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Process {pid} showed no top-level window within {TestTimeouts.Scale(WindowTimeout).TotalSeconds:0.###} s."));
        }
        return new WindowsAccessibility(window!);
    }

    private WindowsAccessibility(AutomationElement window)
    {
        _window = window;
        _valueChanged = (sender, args) => Raise(EventKind.ValueChanged, sender, Convert.ToString(args.NewValue, CultureInfo.InvariantCulture) ?? "");
        Automation.AddAutomationPropertyChangedEventHandler(_window, TreeScope.Subtree, _valueChanged,
            RangeValuePattern.ValueProperty, ValuePattern.ValueProperty);
        Add(SelectionItemPattern.ElementSelectedEvent, (sender, _) => RaiseSelection(sender));
        Add(SelectionPattern.InvalidatedEvent, (sender, _) => RaiseSelection(sender));
        Add(AutomationElementIdentifiers.LiveRegionChangedEvent, (sender, _) => Raise(EventKind.LiveRegion, sender, Text(sender)));
        Add(AutomationElement.NotificationEvent, (sender, args) =>
            Raise(EventKind.Announcement, sender, args is NotificationEventArgs notification
                ? $"{notification.NotificationKind} {notification.NotificationProcessing} {notification.DisplayString}" : ""));
        _focusChanged = (sender, _) => Raise(EventKind.FocusChanged, sender, "");
        Automation.AddAutomationFocusChangedEventHandler(_focusChanged);
    }

    internal override string Api => "UI Automation";

    private void Add(AutomationEvent automationEvent, AutomationEventHandler handler)
    {
        Automation.AddAutomationEventHandler(automationEvent, _window, TreeScope.Subtree, handler);
        _handlers.Add((automationEvent, handler));
    }

    private void Raise(EventKind kind, object sender, string detail)
    {
        if (sender is not AutomationElement element) return;
        try { Raise(new AccessibilityEvent(kind, element.Current.Name, element.Current.ControlType.ProgrammaticName, detail)); }
        catch (ElementNotAvailableException) { }
    }

    // ElementSelected comes from the item; report it as the list's selection change, as a screen reader reads it.
    private void RaiseSelection(object sender)
    {
        if (sender is not AutomationElement element) return;
        try
        {
            var list = element.Current.ControlType == ControlType.ListItem ? TreeWalker.ControlViewWalker.GetParent(element) : element;
            Raise(EventKind.SelectionChanged, list, string.Join(", ", Selection(list)) + $" (from {element.Current.Name})");
        }
        catch (ElementNotAvailableException) { }
    }

    internal override Task<string> WindowTitleAsync(CancellationToken cancellationToken)
        => Task.FromResult($"{_window.Current.Name} ({_window.Current.ControlType.ProgrammaticName})");

    internal override async Task<Control?> FindAsync(ControlKind kind, string name, CancellationToken cancellationToken)
    {
        ControlType[] types = kind switch
        {
            ControlKind.Button => [ControlType.Button],
            ControlKind.Slider => [ControlType.Slider],
            ControlKind.SpinButton => [ControlType.Spinner],
            ControlKind.ListBox => [ControlType.List],
            _ => [],
        };
        var condition = new AndCondition(new PropertyCondition(AutomationElement.NameProperty, name),
            new OrCondition([.. types.Select(type => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, type)), Condition.FalseCondition]));
        // The page loads after the window shows.
        var wait = TestTimeouts.Start(FindTimeout);
        while (true)
        {
            var matches = _window.FindAll(TreeScope.Descendants, condition);
            if (matches.Count == 1)
            {
                wait.Succeeded();
                return new Control(kind, name, matches[0].Current.ControlType.ProgrammaticName + $" ({matches[0].Current.LocalizedControlType})", matches[0]);
            }
            if (matches.Count > 1) throw new InvalidOperationException($"{matches.Count} elements match {kind} '{name}'.");
            if (wait.IsExpired) return null;
            await Task.Delay(200, cancellationToken);
        }
    }

    internal override Task<RangeState> ReadRangeAsync(Control control, CancellationToken cancellationToken)
    {
        var element = (AutomationElement)control.Handle;
        if (element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var pattern))
        {
            var range = ((RangeValuePattern)pattern).Current;
            return Task.FromResult(new RangeState(range.Value, range.Minimum, range.Maximum));
        }
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)
            && double.TryParse(((ValuePattern)pattern).Current.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return Task.FromResult(new RangeState(value, null, null));
        }
        return Task.FromResult(new RangeState(null, null, null));
    }

    // Narrator and NVDA change a slider with the arrow keys, which RangeValue.SetValue stands for.
    internal override Task ChangeRangeAsync(Control control, double value, CancellationToken cancellationToken)
    {
        RangeValue(control).SetValue(value);
        return Task.CompletedTask;
    }

    internal override async Task StepAsync(Control control, int direction, CancellationToken cancellationToken)
    {
        var pattern = RangeValue(control);
        var step = pattern.Current.SmallChange > 0 ? pattern.Current.SmallChange : 1;
        var before = pattern.Current.Value;
        pattern.SetValue(before + direction * step);
        for (var attempt = 0; attempt < 10 && pattern.Current.Value == before; attempt++) await Task.Delay(100, cancellationToken);
        // Narrator and NVDA change a web page's ARIA spin button with the arrow keys; RangeValue.SetValue reaches only
        // native controls.
        if (pattern.Current.Value == before)
            throw new NotSupportedException("WebView2 ignores RangeValue.SetValue on an ARIA spin button; screen readers send arrow keys");
    }

    private static RangeValuePattern RangeValue(Control control)
        => ((AutomationElement)control.Handle).TryGetCurrentPattern(RangeValuePattern.Pattern, out var pattern)
            ? (RangeValuePattern)pattern
            : throw new InvalidOperationException($"'{control.Name}' has no RangeValue pattern.");

    internal override Task PressAsync(Control control, CancellationToken cancellationToken)
    {
        ((InvokePattern)((AutomationElement)control.Handle).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        return Task.CompletedTask;
    }

    // NVDA users change a slider or spin button and a list box's selection with the keyboard, which reaches the page.
    // The speech checks do the same: when WebView2 carries out a UI Automation action from another process, it activates
    // its own window, which deactivates and reactivates the application's window, and NVDA leaves the control, reads the
    // document again and can miss the change it is checked for (I-185). The control has the focus.
    internal override bool ActsWithKeys => true;

    internal override Task StepWithKeysAsync(Control control, int direction, CancellationToken cancellationToken)
    {
        Press(direction > 0 ? VirtualKeyUp : VirtualKeyDown);
        return Task.CompletedTask;
    }

    internal override Task SelectWithKeysAsync(Control control, string item, CancellationToken cancellationToken)
    {
        var items = ((AutomationElement)control.Handle).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().Select(element => element.Current.Name).ToList();
        var index = items.IndexOf(item);
        if (index < 0) throw new InvalidOperationException($"'{control.Name}' has no item '{item}'.");
        Press(VirtualKeyHome);
        for (var step = 0; step < index; step++) Press(VirtualKeyDown);
        return Task.CompletedTask;
    }

    private const ushort VirtualKeyHome = 0x24;
    private const ushort VirtualKeyUp = 0x26;
    private const ushort VirtualKeyDown = 0x28;

    // A key press and release to the window with the keyboard focus. Home and the arrows are extended keys.
    private static void Press(ushort key)
    {
        const uint InputKeyboard = 1;
        const uint ExtendedKey = 1;
        const uint KeyUp = 2;
        Input[] inputs =
        [
            new() { Type = InputKeyboard, Keyboard = new KeyboardInput { VirtualKey = key, Flags = ExtendedKey } },
            new() { Type = InputKeyboard, Keyboard = new KeyboardInput { VirtualKey = key, Flags = ExtendedKey | KeyUp } },
        ];
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException($"SendInput failed with error {Marshal.GetLastPInvokeError()}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    // INPUT: the type, then the union of MOUSEINPUT (the largest member), KEYBDINPUT and HARDWAREINPUT.
    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal KeyboardInput Keyboard;
        [FieldOffset(32)] private readonly long _mouseInputEnd;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    internal override Task<IReadOnlyList<string>> ReadSelectionAsync(Control control, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>(Selection((AutomationElement)control.Handle));

    internal override Task SelectAsync(Control control, string item, CancellationToken cancellationToken)
    {
        var element = ((AutomationElement)control.Handle).FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.NameProperty, item),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
            ?? throw new InvalidOperationException($"'{control.Name}' has no item '{item}'.");
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        return Task.CompletedTask;
    }

    internal override Task FocusAsync(Control control, CancellationToken cancellationToken)
    {
        ((AutomationElement)control.Handle).SetFocus();
        return Task.CompletedTask;
    }

    private static List<string> Selection(AutomationElement list)
    {
        if (list.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern))
            return ((SelectionPattern)pattern).Current.GetSelection().Select(item => item.Current.Name).ToList();
        return list.FindAll(TreeScope.Children, new PropertyCondition(SelectionItemPattern.IsSelectedProperty, true))
            .Cast<AutomationElement>().Select(item => item.Current.Name).ToList();
    }

    private static string Text(object sender)
    {
        if (sender is not AutomationElement element) return "";
        try
        {
            var texts = element.FindAll(TreeScope.Subtree, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Select(text => text.Current.Name);
            return string.Join(" ", texts.Prepend(element.Current.Name).Distinct());
        }
        catch (ElementNotAvailableException) { return ""; }
    }

    public override ValueTask DisposeAsync()
    {
        Automation.RemoveAllEventHandlers();
        return ValueTask.CompletedTask;
    }
}
