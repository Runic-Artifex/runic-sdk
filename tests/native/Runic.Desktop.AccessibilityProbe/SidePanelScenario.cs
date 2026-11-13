// --side-panel reads the side panel fixture instead (Runic.Desktop.WebViewSmoke/AccessibilitySmoke.cs, SidePanelPage,
// sdk#864 P6): a window whose page has a side panel open on its right edge. The panel's page is a second web view of
// the window, with a complementary landmark named "Debug tools". A screen reader meets both views in one window:
// - side-panel-control: the panel's button is in the window's accessibility tree, beside the page's;
// - side-panel-landmark: the panel's landmark is exposed with its name, before the panel's button;
// - side-panel-order-right / -left: the tree (what a screen reader's review walks) has the page before the panel on the
//   right edge and the panel before the page once the fixture docked it on the left edge (the page's Dock panel left);
// - with --screen-reader, <reader>-side-panel-focus, -landmark and -return: focus moved into the panel is spoken with
//   the button's name and the landmark's name, and focus moved back to the page is spoken with the page's button.
// A host that refuses the panel (Unsupported) shows the page's "Side panel unsupported" button instead: the checks are
// then not applicable, because that platform's host has no panel yet (macOS until #905, Linux until #907).
internal sealed class SidePanelScenario(AccessibilityClient client, Report report, ScreenReader? reader)
{
    internal const string PageButton = "Page action";
    internal const string PanelButton = "Panel action";
    internal const string Landmark = "Debug tools";
    internal const string DockLeft = "Dock panel left";
    internal const string Unsupported = "Side panel unsupported";
    private static readonly string[] Checks =
        ["side-panel-control", "side-panel-landmark", "side-panel-order-right", "side-panel-order-left"];
    private static readonly string[] SpeechChecks = ["side-panel-focus", "side-panel-landmark", "side-panel-return"];
    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SpeechTimeout = TimeSpan.FromSeconds(10);
    private readonly List<string> _phrases = [];
    private readonly Lock _lock = new();

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var page = await FindAsync(PageButton, cancellationToken)
            ?? throw new InvalidOperationException($"No button named '{PageButton}' in the side panel fixture.");
        report.Pass("side-panel-page-control", $"{page.Name}: {page.Role}");
        // The panel loads after the page; the fixture shows the other button when the host refuses it.
        Control? panel = null, unsupported = null;
        var until = DateTime.UtcNow + TestTimeouts.Scale(FindTimeout);
        while (panel is null && unsupported is null)
        {
            panel = await client.FindAsync(ControlKind.Button, PanelButton, cancellationToken);
            unsupported = panel is null ? await client.FindAsync(ControlKind.Button, Unsupported, cancellationToken) : null;
            if (panel is null && unsupported is null && DateTime.UtcNow >= until)
            {
                Console.WriteLine("Accessibility tree:\n" + await client.DumpAsync(cancellationToken));
                throw new InvalidOperationException($"Neither '{PanelButton}' nor '{Unsupported}' in the accessibility tree of the side panel fixture.");
            }
        }
        if (unsupported is not null)
        {
            const string reason = "this platform's host refuses the side panel (Unsupported): macOS until #905, Linux until #907";
            foreach (var check in Checks) report.NotApplicable(check, reason);
            if (reader is not null) foreach (var check in SpeechChecks) report.NotApplicable($"{reader.Id}-{check}", reason);
            await client.PressAsync(unsupported, cancellationToken);
            return;
        }
        report.Pass("side-panel-control", $"{panel!.Name}: {panel.Role}, in the panel's view of the same window");

        var elements = await ElementsAsync(element => element.Name == PanelButton, cancellationToken);
        var landmark = elements.FindIndex(element => element.Name == Landmark && !element.Role.Contains("button", StringComparison.OrdinalIgnoreCase));
        var panelIndex = elements.FindIndex(element => element.Name == PanelButton);
        if (landmark >= 0 && landmark < panelIndex)
            report.Pass("side-panel-landmark", $"'{Landmark}': {elements[landmark].Role}, before the panel's button");
        else report.Fail("side-panel-landmark", $"no element named '{Landmark}' before the panel's button: {Describe(elements)}");
        Order("side-panel-order-right", elements, panelFirst: false);

        if (reader is not null) await SpeakAsync(page, panel, cancellationToken);

        // The fixture moves the panel to the left edge; the panel's view then comes before the page's.
        await client.PressAsync(await FindAsync(DockLeft, cancellationToken)
            ?? throw new InvalidOperationException($"No button named '{DockLeft}'."), cancellationToken);
        var moved = DateTime.UtcNow + TestTimeouts.Scale(TimeSpan.FromSeconds(10));
        while (true)
        {
            elements = (await client.ElementsAsync(cancellationToken)).ToList();
            if (IsBefore(elements, PanelButton, PageButton) || DateTime.UtcNow >= moved) break;
            await Task.Delay(250, cancellationToken);
        }
        Order("side-panel-order-left", elements, panelFirst: true);
        await client.PressAsync(await FindAsync(Fixture.Finish, cancellationToken)
            ?? throw new InvalidOperationException($"No button named '{Fixture.Finish}'."), cancellationToken);
    }

    // The tree once it lists the element the condition looks for: a web view's tree can join the window's later.
    private async Task<List<Element>> ElementsAsync(Func<Element, bool> condition, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + TestTimeouts.Scale(TimeSpan.FromSeconds(10));
        while (true)
        {
            var elements = (await client.ElementsAsync(cancellationToken)).ToList();
            if (elements.Any(condition) || DateTime.UtcNow >= until) return elements;
            await Task.Delay(250, cancellationToken);
        }
    }

    private void Order(string check, IReadOnlyList<Element> elements, bool panelFirst)
    {
        var expected = panelFirst ? IsBefore(elements, PanelButton, PageButton) : IsBefore(elements, PageButton, PanelButton);
        var detail = panelFirst ? "the panel's button before the page's (left edge)" : "the page's button before the panel's (right edge)";
        if (expected) report.Pass(check, detail);
        else report.Fail(check, $"expected {detail}: {Describe(elements)}");
    }

    private static bool IsBefore(IReadOnlyList<Element> elements, string first, string second)
    {
        var a = elements.ToList().FindIndex(element => element.Name == first);
        var b = elements.ToList().FindIndex(element => element.Name == second);
        return a >= 0 && b >= 0 && a < b;
    }

    // The named elements in tree order, for the receipt.
    private static string Describe(IReadOnlyList<Element> elements) =>
        string.Join(" | ", elements.Where(static element => !string.IsNullOrWhiteSpace(element.Name)).Select(static element => $"{element.Role} '{element.Name}'"));

    private async Task<Control?> FindAsync(string name, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + TestTimeouts.Scale(FindTimeout);
        while (true)
        {
            if (await client.FindAsync(ControlKind.Button, name, cancellationToken) is { } control) return control;
            if (DateTime.UtcNow >= until) return null;
            await Task.Delay(250, cancellationToken);
        }
    }

    // Focus moves into the panel and back, as Tab does; the screen reader names what gets the focus, and the landmark it
    // enters.
    private async Task SpeakAsync(Control page, Control panel, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var listening = reader!.ListenAsync(Heard, stop.Token);
        try
        {
            await client.FocusAsync(page, cancellationToken);
            await QuietAsync(cancellationToken);
            var mark = Mark();
            await client.FocusAsync(panel, cancellationToken);
            if (await HeardAsync(mark, PanelButton, cancellationToken) is null)
            {
                // A screen reader can miss the first focus change after it read the document; once more from the page.
                await client.FocusAsync(page, cancellationToken);
                await Task.Delay(1000, cancellationToken);
                await QuietAsync(cancellationToken);
                mark = Mark();
                await client.FocusAsync(panel, cancellationToken);
            }
            await RecordAsync("side-panel-focus", PanelButton, mark, cancellationToken);
            await RecordAsync("side-panel-landmark", Landmark, mark, cancellationToken);
            await QuietAsync(cancellationToken);
            mark = Mark();
            await client.FocusAsync(page, cancellationToken);
            await RecordAsync("side-panel-return", PageButton, mark, cancellationToken);
        }
        finally
        {
            await stop.CancelAsync();
            await listening;
            lock (_lock) Console.WriteLine($"{reader.Name} phrases: " + string.Join(" | ", _phrases));
        }
    }

    private DateTime _lastPhrase = DateTime.UtcNow;

    private void Heard(string phrase)
    {
        lock (_lock)
        {
            _phrases.Add(phrase);
            _lastPhrase = DateTime.UtcNow;
        }
        Console.WriteLine($"  {reader!.Name}: {phrase}");
    }

    private int Mark()
    {
        lock (_lock) return reader!.Sampled ? Math.Max(0, _phrases.Count - 1) : _phrases.Count;
    }

    // Until the screen reader has said nothing for 1.5 s, at most 8 s.
    private async Task QuietAsync(CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < until)
        {
            lock (_lock) if (DateTime.UtcNow - _lastPhrase > TimeSpan.FromMilliseconds(1500)) return;
            await Task.Delay(100, cancellationToken);
        }
    }

    private async Task<string?> HeardAsync(int mark, string text, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + SpeechTimeout;
        while (true)
        {
            lock (_lock)
            {
                if (_phrases.Skip(mark).FirstOrDefault(phrase => phrase.Contains(text, StringComparison.Ordinal)) is { } spoken) return spoken;
            }
            if (DateTime.UtcNow >= until) return null;
            await Task.Delay(100, cancellationToken);
        }
    }

    private async Task RecordAsync(string check, string text, int mark, CancellationToken cancellationToken)
    {
        var id = $"{reader!.Id}-{check}";
        var spoken = await HeardAsync(mark, text, cancellationToken);
        string heard;
        lock (_lock) heard = string.Join(" | ", _phrases.Skip(mark));
        if (spoken is not null) report.Pass(id, $"{reader.Name} said '{spoken}'");
        else report.Fail(id, $"{reader.Name} did not say '{text}'; it said: {heard}");
    }
}
