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

            await ExpectAnnouncementAsync("announcement-polite", Fixture.PoliteAnnouncement, Fixture.Announce, cancellationToken);
            await ExpectAnnouncementAsync("announcement-assertive", Fixture.AssertiveAnnouncement, Fixture.Alert, cancellationToken);
            await ExpectAsync("live-region", Contains(Fixture.Saved(2)),
                async () => await client.PressAsync(await FindAsync(ControlKind.Button, Fixture.Save, cancellationToken), cancellationToken),
                cancellationToken);

            // The screen reader follows the keyboard focus and reads the control; then the value the user changes, as the
            // screen reader's users change it (StepWithKeysAsync). The value must be 6: a slider left at 7 or never read
            // fails.
            await FocusAsync(slider, keep, "slider-focus", Fixture.Volume, cancellationToken);
            await SettleAsync(slider, cancellationToken);
            await ExpectStepAsync("slider-value", Value("6"), slider, -1, cancellationToken);
            await FocusAsync(keep, slider, "spinbutton-focus", Fixture.Keep, cancellationToken);
            try
            {
                // aria-valuetext: without it VoiceOver reads an ARIA spin button's value as a percentage of its range.
                await SettleAsync(keep, cancellationToken);
                await ExpectStepAsync("spinbutton-value", Contains((kept + 1).ToString(CultureInfo.InvariantCulture) + " restore points"),
                    keep, 1, cancellationToken);
            }
            catch (NotSupportedException reason)
            {
                report.NotApplicable(Id("spinbutton-value"), reason.Message);
            }
            await FocusAsync(fruit, slider, "listbox-focus", Fixture.Fruit, cancellationToken);
            await SettleAsync(fruit, cancellationToken);
            await ExpectAsync("listbox-selection", Contains("Apple"), () => client.SelectWithKeysAsync(fruit, "Apple", cancellationToken), cancellationToken);
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
        lock (_lock)
        {
            _phrases.Add(phrase);
            _lastPhrase = DateTime.UtcNow;
        }
        Console.WriteLine($"  {reader.Name}: {phrase}");
    }

    private DateTime _lastPhrase = DateTime.UtcNow;
    private int _focusMark;

    // Before a check presses keys on the focused control (ActsWithKeys): since focus moved there, the screen reader has
    // named the control after it last named the page (WebView2 can take the screen reader to the document when it
    // carries out a UI Automation action, I-185), and it has said nothing for a moment. The keys then reach the control
    // rather than the screen reader's review of the document. When the screen reader ends elsewhere, focus moves to
    // the control once more; the check itself has no retry, so it hears the change or fails.
    private async Task SettleAsync(Control control, CancellationToken cancellationToken)
    {
        if (!client.ActsWithKeys) return;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < until)
            {
                bool settled;
                lock (_lock)
                {
                    var since = _phrases.Skip(_focusMark).ToList();
                    settled = since.FindLastIndex(phrase => phrase.Contains(control.Name, StringComparison.Ordinal))
                            > since.FindLastIndex(phrase => phrase.Contains(Fixture.Title, StringComparison.Ordinal))
                        && DateTime.UtcNow - _lastPhrase > TimeSpan.FromMilliseconds(800);
                }
                if (settled) return;
                await Task.Delay(100, cancellationToken);
            }
            if (attempt == 0)
            {
                Console.WriteLine($"  {reader.Name} did not settle on '{control.Name}'; focusing it again before the check.");
                _focusMark = Mark();
                await client.FocusAsync(control, cancellationToken);
            }
        }
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
        var mark = _focusMark = Mark();
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

    // Acts, then checks what the screen reader said from just before the action on.
    private async Task ExpectAsync(string check, Expected expected, Func<Task> action, CancellationToken cancellationToken)
    {
        var mark = Mark();
        await action();
        await RecordAsync(check, expected, mark, cancellationToken);
    }

    // An announcement made while the screen reader is still speaking can be lost: VoiceOver's sampled phrase showed only
    // the hints around it, and NVDA once said nothing (I-166). The press waits until the screen reader has been quiet for
    // a moment; an announcement it still does not say is announced once more, and the check reports the second press.
    private async Task ExpectAnnouncementAsync(string check, string text, string button, CancellationToken cancellationToken)
    {
        var expected = Contains(text);
        for (var press = 1; press <= 2; press++)
        {
            await QuietAsync(cancellationToken);
            var mark = Mark();
            await client.PressAsync(await FindAsync(ControlKind.Button, button, cancellationToken), cancellationToken);
            if (press == 1 && await HeardAsync(mark, expected, SpeechTimeout, cancellationToken) is null)
            {
                string heard;
                lock (_lock) heard = string.Join(" | ", _phrases.Skip(mark));
                Console.WriteLine($"  {reader.Name} did not say '{text}' after the first press (it said: {heard}); pressing '{button}' again.");
                continue;
            }
            await RecordAsync(check, expected, mark, cancellationToken, press == 2 ? " on the second press" : "");
            return;
        }
    }

    // A step made while the screen reader is still between a control's name and its hint can go unsaid: VoiceOver read
    // the stepper's hint instead of "8 restore points" (I-166, PR #620). The step waits until the screen reader has been
    // quiet for a moment. When the screen reader does not say the new value, the step is undone and made once more from
    // the same value, so the check still needs the screen reader to say the value after a real step; it reports the
    // second step. A step whose undo does not restore the value is not repeated and fails on what the first step heard.
    private async Task ExpectStepAsync(string check, Expected expected, Control control, int direction, CancellationToken cancellationToken)
    {
        var before = (await client.ReadRangeAsync(control, cancellationToken)).Value;
        for (var step = 1; step <= 2; step++)
        {
            await QuietAsync(cancellationToken);
            var mark = Mark();
            await client.StepWithKeysAsync(control, direction, cancellationToken);
            if (step == 1 && await HeardAsync(mark, expected, SpeechTimeout, cancellationToken) is null)
            {
                string heard;
                lock (_lock) heard = string.Join(" | ", _phrases.Skip(mark));
                var after = (await client.ReadRangeAsync(control, cancellationToken)).Value;
                if (after != before)
                {
                    await client.StepWithKeysAsync(control, -direction, cancellationToken);
                    after = (await client.ReadRangeAsync(control, cancellationToken)).Value;
                }
                if (after == before)
                {
                    Console.WriteLine($"  {reader.Name} did not say '{expected.Text}' after the first step (it said: {heard}); stepping '{control.Name}' again from {before}.");
                    continue;
                }
                Console.WriteLine($"  '{control.Name}' is {after} after undoing the first step, not {before}; not stepping again.");
            }
            await RecordAsync(check, expected, mark, cancellationToken, step == 2 ? " on the second step" : "");
            return;
        }
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

    private async Task RecordAsync(string check, Expected expected, int mark, CancellationToken cancellationToken, string note = "")
    {
        var id = Id(check);
        var spoken = await HeardAsync(mark, expected, SpeechTimeout, cancellationToken);
        var gap = expectations.Gap(id);
        string heard;
        lock (_lock) heard = string.Join(" | ", _phrases.Skip(mark));
        if (spoken is not null && gap is null) report.Pass(id, $"{reader.Name} said '{spoken}'{note}");
        else if (spoken is not null) report.Pass(id, $"{reader.Name} said '{spoken}'{note} (listed as a known gap: {gap}; update Expectations.cs and the documentation)");
        else if (gap is not null) report.Gap(id, $"{reader.Name} did not say '{expected.Text}'; it said: {heard}", gap);
        else report.Fail(id, $"{reader.Name} did not say '{expected.Text}'; it said: {heard}");
    }
}
