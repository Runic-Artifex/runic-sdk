using System.Globalization;

// The names and values of the fixture page (Runic.Desktop.WebViewSmoke/AccessibilitySmoke.cs).
internal static class Fixture
{
    internal const string Title = "Runic accessibility fixture";
    internal const string Volume = "Volume";
    internal const string Copies = "Copies";
    internal const string Keep = "Restore points to keep";
    internal const string KeepMore = "Keep more";
    internal const string Fruit = "Fruit";
    internal const string Colour = "Colour";
    internal const string NextColour = "Next colour";
    internal const string Save = "Save";
    internal const string Announce = "Announce";
    internal const string Alert = "Alert";
    internal const string Finish = "Finish";
    internal const string PoliteAnnouncement = "Runic polite announcement";
    internal const string AssertiveAnnouncement = "Runic assertive announcement";
    internal static string Saved(int version) => "Saved version " + version.ToString(CultureInfo.InvariantCulture);
}

// The checks, in order. Each reads or acts through the accessibility API only; the page reacts as it would to a user.
internal sealed class Scenario(AccessibilityClient client, Report report, Expectations expectations) : IDisposable
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(5);
    private readonly EventLog _log = new(client);
    private readonly Dictionary<string, Control> _controls = [];

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        await FindAllAsync(cancellationToken);
        // The window is named by the page's <title>, which the page has once its controls are there.
        var title = "";
        for (var attempt = 0; attempt < 20 && !title.Contains(Fixture.Title, StringComparison.Ordinal); attempt++)
        {
            if (attempt > 0) await Task.Delay(100, cancellationToken);
            title = await client.WindowTitleAsync(cancellationToken);
        }
        Record("window-title", title.Contains(Fixture.Title, StringComparison.Ordinal), $"window named '{title}'");
        var menu = await client.MenuBarAsync(cancellationToken);
        if (menu is null) report.NotApplicable("menu-bar", "the host has no native menu bar");
        else Record("menu-bar", menu.Contains("Edit", StringComparison.Ordinal) && menu.Contains("View", StringComparison.Ordinal), menu);

        var slider = _controls[Fixture.Volume];
        var copies = _controls[Fixture.Copies];
        var keep = _controls[Fixture.Keep];
        var fruit = _controls[Fixture.Fruit];
        var colour = _controls[Fixture.Colour];

        await ExpectRangeAsync("slider-range", slider, 3, 0, 10, cancellationToken);
        await ExpectRangeAsync("number-range", copies, 2, 1, 9, cancellationToken);
        await ExpectRangeAsync("spinbutton-range", keep, 5, 1, 50, cancellationToken);
        await ExpectSelectionAsync("listbox-selection", fruit, "Pear", cancellationToken);
        await ExpectSelectionAsync("aria-listbox-selection", colour, "Red", cancellationToken);

        // A range the user changes: the screen reader sets it, or increments it, and hears the new value. A value
        // event names its element; not every API carries the new value with it, so the value is read separately.
        var mark = _log.Mark();
        await client.ChangeRangeAsync(slider, 7, cancellationToken);
        await ExpectValueAsync("slider-change", slider, 7, cancellationToken);
        await ExpectEventAsync("slider-change-event", mark, EventKind.ValueChanged, Fixture.Volume, null, cancellationToken);

        // An ARIA spin button: the screen reader's increment reaches the page as an arrow key.
        var kept = 5;
        mark = _log.Mark();
        try
        {
            await client.StepAsync(keep, 1, cancellationToken);
            kept++;
            await ExpectValueAsync("spinbutton-increment", keep, kept, cancellationToken);
            await ExpectEventAsync("spinbutton-increment-event", mark, EventKind.ValueChanged, Fixture.Keep, null, cancellationToken);
        }
        catch (NotSupportedException reason)
        {
            report.NotApplicable("spinbutton-increment", reason.Message);
        }

        // A value the page changes (a button beside the spin button): the screen reader hears it.
        mark = _log.Mark();
        await client.PressAsync(_controls[Fixture.KeepMore], cancellationToken);
        kept++;
        await ExpectValueAsync("spinbutton-page-change", keep, kept, cancellationToken);
        await ExpectEventAsync("spinbutton-page-change-event", mark, EventKind.ValueChanged, Fixture.Keep, null, cancellationToken);
        Kept = kept;

        // A native list box: the screen reader selects an item.
        mark = _log.Mark();
        await client.SelectAsync(fruit, "Plum", cancellationToken);
        await ExpectSelectionAsync("listbox-select", fruit, "Plum", cancellationToken);
        await ExpectEventAsync("listbox-select-event", mark, EventKind.SelectionChanged, Fixture.Fruit, "Plum", cancellationToken);

        // An ARIA list box whose selection the page moves (aria-selected).
        mark = _log.Mark();
        await client.PressAsync(_controls[Fixture.NextColour], cancellationToken);
        await ExpectSelectionAsync("aria-listbox-page-change", colour, "Green", cancellationToken);
        await ExpectEventAsync("aria-listbox-page-change-event", mark, EventKind.SelectionChanged, Fixture.Colour, "Green", cancellationToken);

        // DesktopWindow.AnnounceAsync: the page asks the host, which announces with each priority. On WebView2, while the
        // page has the keyboard focus, the page's Bridge says it in a live region instead of the window (I-184).
        mark = _log.Mark();
        await client.PressAsync(_controls[Fixture.Announce], cancellationToken);
        await ExpectEventAsync("announcement-polite", mark, EventKind.Announcement, null, Fixture.PoliteAnnouncement, cancellationToken,
            alternative: EventKind.LiveRegion);
        mark = _log.Mark();
        await client.PressAsync(_controls[Fixture.Alert], cancellationToken);
        await ExpectEventAsync("announcement-assertive", mark, EventKind.Announcement, null, Fixture.AssertiveAnnouncement, cancellationToken,
            alternative: EventKind.LiveRegion);

        // A live region (aria-live="polite" on an output).
        mark = _log.Mark();
        await client.PressAsync(_controls[Fixture.Save], cancellationToken);
        await ExpectEventAsync("live-region", mark, EventKind.LiveRegion, null, Fixture.Saved(1), cancellationToken);

        // Focus moved by the application (as after navigation) reaches the screen reader.
        mark = _log.Mark();
        await client.FocusAsync(slider, cancellationToken);
        await ExpectEventAsync("focus-event", mark, EventKind.FocusChanged, Fixture.Volume, null, cancellationToken);
    }

    /// <summary>The ARIA spin button's value after the checks.</summary>
    internal int Kept { get; private set; }

    public void Dispose() => _log.Dispose();

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    internal Task FinishAsync(CancellationToken cancellationToken)
        => client.PressAsync(_controls[Fixture.Finish], cancellationToken);

    private async Task FindAllAsync(CancellationToken cancellationToken)
    {
        (ControlKind Kind, string Name)[] wanted =
        [
            (ControlKind.Slider, Fixture.Volume), (ControlKind.SpinButton, Fixture.Copies), (ControlKind.SpinButton, Fixture.Keep),
            (ControlKind.ListBox, Fixture.Fruit), (ControlKind.ListBox, Fixture.Colour), (ControlKind.Button, Fixture.KeepMore),
            (ControlKind.Button, Fixture.NextColour), (ControlKind.Button, Fixture.Save), (ControlKind.Button, Fixture.Announce),
            (ControlKind.Button, Fixture.Alert), (ControlKind.Button, Fixture.Finish),
        ];
        foreach (var (kind, name) in wanted)
        {
            var control = await client.FindAsync(kind, name, cancellationToken);
            if (control is null)
            {
                Console.WriteLine("Accessibility tree:\n" + await client.DumpAsync(cancellationToken));
                throw new InvalidOperationException($"No {kind} named '{name}' in the accessibility tree.");
            }
            _controls[name] = control;
        }
        report.Pass("roles", string.Join("; ", _controls.Values.Select(control => $"{control.Name}: {control.Role}")));
    }

    private async Task ExpectRangeAsync(string id, Control control, double value, double minimum, double maximum,
        CancellationToken cancellationToken)
    {
        var range = await client.ReadRangeAsync(control, cancellationToken);
        Record(id, range.Value == value && range.Minimum == minimum && range.Maximum == maximum, $"{control.Name} ({control.Role}): {range}");
    }

    private async Task ExpectValueAsync(string id, Control control, double value, CancellationToken cancellationToken)
    {
        RangeState range = default;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            range = await client.ReadRangeAsync(control, cancellationToken);
            if (range.Value == value) break;
            await Task.Delay(100, cancellationToken);
        }
        Record(id, range.Value == value, $"{control.Name}: {range}");
    }

    private async Task ExpectSelectionAsync(string id, Control control, string item, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> selection = [];
        for (var attempt = 0; attempt < 20; attempt++)
        {
            selection = await client.ReadSelectionAsync(control, cancellationToken);
            if (selection.Count == 1 && selection[0] == item) break;
            await Task.Delay(100, cancellationToken);
        }
        Record(id, selection.Count == 1 && selection[0] == item, $"{control.Name} selection: [{string.Join(", ", selection)}]");
    }

    // An event of the kind after the mark, from the named element, whose source or data contains the text.
    private async Task ExpectEventAsync(string id, int mark, EventKind kind, string? source, string? text,
        CancellationToken cancellationToken, EventKind? alternative = null)
    {
        var found = await _log.WaitAsync(mark, value => (value.Kind == kind || value.Kind == alternative)
            && (source is null || value.Source.Contains(source, StringComparison.Ordinal))
            && (text is null || value.Detail.Contains(text, StringComparison.Ordinal) || value.Source.Contains(text, StringComparison.Ordinal)),
            EventTimeout, cancellationToken);
        var seen = _log.Since(mark);
        Record(id, found is not null, found is not null
            ? $"{found.Kind} from '{found.Source}' ({found.Role}): {found.Detail}"
            : $"no {kind} event{(source is null ? "" : $" from '{source}'")}{(text is null ? "" : $" with '{text}'")}; seen: "
                + (seen.Count == 0 ? "none" : string.Join("; ", seen.Select(value => $"{value.Kind} '{value.Source}' {value.Detail}"))));
    }

    private void Record(string id, bool passed, string detail)
    {
        var gap = expectations.Gap(id);
        if (passed && gap is null) report.Pass(id, detail);
        else if (passed) report.Pass(id, detail + $" (listed as a known gap: {gap}; update Expectations.cs and the documentation)");
        else if (gap is not null) report.Gap(id, detail, gap);
        else report.Fail(id, detail);
    }
}

// Collects the client's events so that a check can look at those after its action, in any order.
internal sealed class EventLog : IDisposable
{
    private readonly List<AccessibilityEvent> _events = [];
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _added = new(0);

    internal EventLog(AccessibilityClient client)
    {
        _ = Task.Run(async () =>
        {
            await foreach (var value in client.Events.ReadAllAsync())
            {
                lock (_lock) _events.Add(value);
                _added.Release();
            }
        });
    }

    public void Dispose() => _added.Dispose();

    internal int Mark()
    {
        lock (_lock) return _events.Count;
    }

    internal IReadOnlyList<AccessibilityEvent> Since(int mark)
    {
        lock (_lock) return _events.Skip(mark).ToArray();
    }

    internal async Task<AccessibilityEvent?> WaitAsync(int mark, Func<AccessibilityEvent, bool> predicate, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + TestTimeouts.Scale(timeout);
        while (true)
        {
            var found = Since(mark).FirstOrDefault(predicate);
            if (found is not null) return found;
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            await _added.WaitAsync(left < TimeSpan.FromMilliseconds(200) ? left : TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }
}
