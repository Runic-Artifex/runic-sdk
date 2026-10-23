using System.Globalization;

// What a running screen reader speaks while the probe acts on the page through the accessibility API: VoiceOver on
// macOS, NVDA on Windows, Orca on Linux (--screen-reader). The checks are named after the screen reader
// (voiceover-*, nvda-*, orca-*); Expectations.cs lists what each one is known not to say.
internal sealed class SpeechScenario(AccessibilityClient client, Report report, Expectations expectations, ScreenReader reader, int kept)
{
    private static readonly TimeSpan SpeechTimeout = TimeSpan.FromSeconds(10);
    private readonly List<string> _phrases = [];
    private readonly Lock _lock = new();

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var listening = reader.ListenAsync(Heard, stop.Token);
        try
        {
            var slider = await FindAsync(ControlKind.Slider, Fixture.Volume, cancellationToken);
            var keep = await FindAsync(ControlKind.SpinButton, Fixture.Keep, cancellationToken);
            var fruit = await FindAsync(ControlKind.ListBox, Fixture.Fruit, cancellationToken);

            await ExpectAsync("announcement-polite", Contains(Fixture.PoliteAnnouncement),
                async () => await client.PressAsync(await FindAsync(ControlKind.Button, Fixture.Announce, cancellationToken), cancellationToken),
                cancellationToken);
            await ExpectAsync("announcement-assertive", Contains(Fixture.AssertiveAnnouncement),
                async () => await client.PressAsync(await FindAsync(ControlKind.Button, Fixture.Alert, cancellationToken), cancellationToken),
                cancellationToken);
            await ExpectAsync("live-region", Contains(Fixture.Saved(2)),
                async () => await client.PressAsync(await FindAsync(ControlKind.Button, Fixture.Save, cancellationToken), cancellationToken),
                cancellationToken);

            // The screen reader follows the keyboard focus and reads the control; then the value the user changes.
            await FocusAsync(slider, keep, "slider-focus", Fixture.Volume, cancellationToken);
            // NVDA can follow WebView2's focus to the document after the step (I-185) and say the new value only once
            // focus returns to the slider. The value must still be 6: a slider left at 7 or never read fails.
            await ExpectAsync("slider-value", Value("6"), () => client.StepAsync(slider, -1, cancellationToken), cancellationToken,
                retry: () => client.FocusAsync(slider, cancellationToken));
            await FocusAsync(keep, slider, "spinbutton-focus", Fixture.Keep, cancellationToken);
            try
            {
                // aria-valuetext: without it VoiceOver reads an ARIA spin button's value as a percentage of its range.
                await ExpectAsync("spinbutton-value", Contains((kept + 1).ToString(CultureInfo.InvariantCulture) + " restore points"),
                    () => client.StepAsync(keep, 1, cancellationToken), cancellationToken);
            }
            catch (NotSupportedException reason)
            {
                report.NotApplicable(Id("spinbutton-value"), reason.Message);
            }
            await FocusAsync(fruit, slider, "listbox-focus", Fixture.Fruit, cancellationToken);
            // NVDA can follow WebView2's focus to the document after the selection and say the item only once focus
            // returns to the list.
            await ExpectAsync("listbox-selection", Contains("Apple"), () => client.SelectAsync(fruit, "Apple", cancellationToken), cancellationToken,
                retry: () => client.FocusAsync(fruit, cancellationToken));
        }
        finally
        {
            await stop.CancelAsync();
            await listening;
            lock (_lock) Console.WriteLine($"{reader.Name} phrases: " + string.Join(" | ", _phrases));
        }
    }

    private string Id(string check) => $"{reader.Id}-{check}";

    private void Heard(string phrase)
    {
        lock (_lock) _phrases.Add(phrase);
        Console.WriteLine($"  {reader.Name}: {phrase}");
    }

    // What a check listens for: a phrase that contains the text, or a value said on its own or last (VoiceOver and Orca
    // say "6", NVDA "Volume slider 6"), so that another control's phrase that contains the digit does not count.
    private sealed record Expected(string Text, Func<string, bool> Matches);

    private static Expected Contains(string text) => new(text, phrase => phrase.Contains(text, StringComparison.Ordinal));

    private static Expected Value(string value) => new(value, phrase =>
    {
        var spoken = phrase.Trim().TrimEnd('.');
        return spoken == value || spoken.EndsWith(" " + value, StringComparison.Ordinal);
    });

    // Where the phrases after an action start. A sampled screen reader's current phrase can already be the action's.
    private int Mark()
    {
        lock (_lock) return reader.Sampled ? Math.Max(0, _phrases.Count - 1) : _phrases.Count;
    }

    // A screen reader says the focused control once, then a hint; a sampled last phrase can miss the first. Focus moves
    // away and back once before the check fails.
    private async Task FocusAsync(Control control, Control other, string check, string name, CancellationToken cancellationToken)
    {
        var mark = Mark();
        var expected = Contains(name);
        await client.FocusAsync(control, cancellationToken);
        if (await HeardAsync(mark, expected, TimeSpan.FromSeconds(4), cancellationToken) is null)
        {
            await client.FocusAsync(other, cancellationToken);
            await Task.Delay(1000, cancellationToken);
            await client.FocusAsync(control, cancellationToken);
        }
        await RecordAsync(check, expected, mark, cancellationToken);
    }

    private async Task<Control> FindAsync(ControlKind kind, string name, CancellationToken cancellationToken)
        => await client.FindAsync(kind, name, cancellationToken) ?? throw new InvalidOperationException($"No {kind} '{name}'.");

    // The first phrase from the mark on that matches, or null after the timeout.
    private async Task<string?> HeardAsync(int mark, Expected expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + timeout;
        string? spoken = null;
        while (spoken is null && DateTime.UtcNow < until)
        {
            lock (_lock) spoken = _phrases.Skip(mark).FirstOrDefault(expected.Matches);
            if (spoken is null) await Task.Delay(100, cancellationToken);
        }
        return spoken;
    }

    // Acts, then checks what the screen reader said from just before the action on. retry, when the screen reader has
    // not said it within a few seconds, makes it read the control again; the receipt says so.
    private async Task ExpectAsync(string check, Expected expected, Func<Task> action, CancellationToken cancellationToken,
        Func<Task>? retry = null)
    {
        var mark = Mark();
        await action();
        var retried = false;
        if (retry is not null && await HeardAsync(mark, expected, TimeSpan.FromSeconds(4), cancellationToken) is null)
        {
            await retry();
            retried = true;
        }
        await RecordAsync(check, expected, mark, cancellationToken, retried ? " after focus returned to the control" : "");
    }

    private async Task RecordAsync(string check, Expected expected, int mark, CancellationToken cancellationToken, string when = "")
    {
        var id = Id(check);
        var spoken = await HeardAsync(mark, expected, SpeechTimeout, cancellationToken);
        var gap = expectations.Gap(id);
        string heard;
        lock (_lock) heard = string.Join(" | ", _phrases.Skip(mark));
        if (spoken is not null && gap is null) report.Pass(id, $"{reader.Name} said '{spoken}'{when}");
        else if (spoken is not null) report.Pass(id, $"{reader.Name} said '{spoken}'{when} (listed as a known gap: {gap}; update Expectations.cs and the documentation)");
        else if (gap is not null) report.Gap(id, $"{reader.Name} did not say '{expected.Text}'; it said: {heard}", gap);
        else report.Fail(id, $"{reader.Name} did not say '{expected.Text}'; it said: {heard}");
    }
}
