using System.Globalization;
using System.Windows.Automation;

// UI Automation, which Narrator and NVDA use: the fixture's top-level window by process ID, WebView2's provider tree
// below it, the RangeValue, Selection, SelectionItem and Invoke patterns, and the property, selection, live region,
// notification and focus events.
internal sealed class WindowsAccessibility : AccessibilityClient
{
    private readonly AutomationElement _window;
    private readonly List<(AutomationEvent Event, AutomationEventHandler Handler)> _handlers = [];
    private readonly AutomationPropertyChangedEventHandler _valueChanged;
    private readonly AutomationFocusChangedEventHandler _focusChanged;

    internal WindowsAccessibility(int pid, Action listening)
    {
        listening();
        AutomationElement? window = null;
        for (var attempt = 0; attempt < 300 && window is null; attempt++)
        {
            window = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
            if (window is null) Thread.Sleep(100);
        }
        _window = window ?? throw new InvalidOperationException($"Process {pid} has no top-level window.");

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
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var matches = _window.FindAll(TreeScope.Descendants, condition);
            if (matches.Count == 1)
                return new Control(kind, name, matches[0].Current.ControlType.ProgrammaticName + $" ({matches[0].Current.LocalizedControlType})", matches[0]);
            if (matches.Count > 1) throw new InvalidOperationException($"{matches.Count} elements match {kind} '{name}'.");
            await Task.Delay(200, cancellationToken);
        }
        return null;
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
