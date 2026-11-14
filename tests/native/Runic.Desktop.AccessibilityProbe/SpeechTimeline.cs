using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

// What a screen reader has said during the speech checks, in order, and when the probe last heard it or acted on the
// page. SpeechScenario.cs waits on it; the times are passed in, so Runic.Desktop.Tests checks the waits without a
// screen reader (AccessibilityProbeSpeechTests.cs).
internal sealed class SpeechTimeline(DateTime start)
{
    private readonly List<string> _phrases = [];
    private readonly Lock _lock = new();
    private DateTime _lastPhrase = start;
    private DateTime _lastAction = start;

    internal void Heard(string phrase, DateTime at)
    {
        lock (_lock)
        {
            _phrases.Add(phrase);
            _lastPhrase = at;
        }
    }

    /// <summary>The probe moved the focus, pressed a key or invoked a control.</summary>
    internal void Acted(DateTime at)
    {
        lock (_lock) _lastAction = at;
    }

    internal int Count
    {
        get { lock (_lock) return _phrases.Count; }
    }

    internal IReadOnlyList<string> Since(int mark)
    {
        lock (_lock) return _phrases.Skip(mark).ToList();
    }

    /// <summary>The phrases from the mark on, as a failure message lists them.</summary>
    internal string Said(int mark) => string.Join(" | ", Since(mark));

    internal string? First(int mark, Func<string, bool> matches)
    {
        lock (_lock) return _phrases.Skip(mark).FirstOrDefault(matches);
    }

    /// <summary>Whether the screen reader has said nothing, and the probe has done nothing, for the window. The screen
    /// reader answers an action after it: measured from the last phrase alone, a check that follows an action at once
    /// (focus moved again) counts the silence before the answer (sdk#988: NVDA said "document" 80 ms after the
    /// focus moved, 870 ms after its last phrase, and the 800 ms quiet window had already passed).</summary>
    internal bool QuietFor(TimeSpan window, DateTime now)
    {
        lock (_lock) return now - (_lastPhrase > _lastAction ? _lastPhrase : _lastAction) >= window;
    }

    /// <summary>Whether, from the mark on, the screen reader last named the control rather than the page: the last phrase
    /// with the control's name comes after the last phrase with the page's (WebView2 can take NVDA to the document,
    /// I-185).</summary>
    internal bool EndsOn(int mark, string control, string page)
    {
        List<string> since;
        lock (_lock) since = _phrases.Skip(mark).ToList();
        var named = since.FindLastIndex(phrase => phrase.Contains(control, StringComparison.Ordinal));
        return named >= 0 && named > since.FindLastIndex(phrase => phrase.Contains(page, StringComparison.Ordinal));
    }
}
