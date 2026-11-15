using TUnit.Core;

namespace Runic.Platform.Runtime.Tests;

// sdk#988: the waits of the accessibility probe's speech checks (tests/native/Runic.Desktop.AccessibilityProbe,
// SpeechTimeline.cs), on the phrases and times NVDA's flaky runs logged.
internal sealed class AccessibilityProbeSpeechTests
{
    private const string Page = "Runic accessibility fixture";
    private static readonly DateTime Start = new(2026, 11, 14, 22, 19, 27, DateTimeKind.Utc);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(1500);

    private static DateTime At(int milliseconds) => Start.AddMilliseconds(milliseconds);

    [Test, DisplayName("SPEECH: quiet counts from the last focus move as well as the last phrase")]
    public void QuietCountsFromTheLastAction()
    {
        // NVDA named the slider; 850 ms later the focus check moved the focus back to it, and NVDA read the document
        // 80 ms after that. 900 ms after the phrase, the old check (800 ms since the last phrase) let the keys go.
        var timeline = new SpeechTimeline(Start);
        timeline.Heard("Volume slider 7", At(0));
        timeline.Acted(At(850));
        Check(!timeline.QuietFor(Quiet, At(900)), "Not quiet 50 ms after the focus moved.");
        Check(!timeline.QuietFor(TimeSpan.FromMilliseconds(800), At(900)), "The 800 ms window counts from the focus move too.");
        Check(!timeline.QuietFor(Quiet, At(850 + 1499)), "Not quiet before the window has passed since the focus move.");
        Check(timeline.QuietFor(Quiet, At(850 + 1500)), "Quiet once the window has passed since the focus move.");

        // A phrase after the action counts from the phrase.
        timeline.Heard("Runic accessibility fixture document ￼", At(930));
        Check(!timeline.QuietFor(Quiet, At(850 + 1500)), "A later phrase restarts the window.");
        Check(timeline.QuietFor(Quiet, At(930 + 1500)), "Quiet once the window has passed since the later phrase.");
    }

    [Test, DisplayName("SPEECH: quiet counts from the start before anything happens")]
    public void QuietCountsFromTheStart()
    {
        var timeline = new SpeechTimeline(Start);
        Check(!timeline.QuietFor(Quiet, At(1000)), "Not quiet 1 s after the start.");
        Check(timeline.QuietFor(Quiet, At(1500)), "Quiet once the window has passed since the start.");
    }

    [Test, DisplayName("SPEECH: the screen reader ends on the control only when it named the control after the page")]
    public void EndsOnTheControl()
    {
        var timeline = new SpeechTimeline(Start);
        timeline.Heard("Save button", At(0));
        var mark = timeline.Count;
        Check(!timeline.EndsOn(mark, "Volume", Page), "Nothing said since the mark.");

        // What NVDA said in run 43646560703: the slider late, on the move away, then the document on the move back.
        timeline.Heard("main landmark", At(100));
        timeline.Heard("Volume slider 7", At(110));
        Check(timeline.EndsOn(mark, "Volume", Page), "NVDA named the slider.");
        timeline.Heard("Restore points to keep spin button 6 restore points", At(160));
        Check(timeline.EndsOn(mark, "Volume", Page), "Another control's phrase does not name the page.");
        timeline.Heard("Runic accessibility fixture document ￼", At(900));
        Check(!timeline.EndsOn(mark, "Volume", Page), "NVDA went to the document after the slider.");

        // Focused once more, NVDA names the slider after the document.
        timeline.Heard("main landmark", At(6000));
        timeline.Heard("Volume slider 7", At(6100));
        Check(timeline.EndsOn(mark, "Volume", Page), "NVDA named the slider after the document.");
        Check(!timeline.EndsOn(timeline.Count, "Volume", Page), "Phrases before the mark do not count.");
    }

    [Test, DisplayName("SPEECH: the phrases from a mark are what the check heard")]
    public void PhrasesFromAMark()
    {
        var timeline = new SpeechTimeline(Start);
        timeline.Heard("Save button", At(0));
        var mark = timeline.Count;
        timeline.Heard("main landmark", At(100));
        timeline.Heard("Restore points to keep spin button 6 restore points", At(110));
        Check(timeline.Said(mark) == "main landmark | Restore points to keep spin button 6 restore points", timeline.Said(mark));
        Check(timeline.First(mark, phrase => phrase.Contains("Volume", StringComparison.Ordinal)) is null, "No phrase names the slider.");
        Check(timeline.First(mark, phrase => phrase.Contains("landmark", StringComparison.Ordinal)) == "main landmark", "The first match from the mark.");
        Check(timeline.Since(0).SequenceEqual(["Save button", "main landmark", "Restore points to keep spin button 6 restore points"]),
            string.Join(" | ", timeline.Since(0)));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
