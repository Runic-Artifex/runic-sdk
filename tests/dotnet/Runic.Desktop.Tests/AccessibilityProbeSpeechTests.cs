using TUnit.Core;
using Xunit;

namespace Runic.Desktop.Tests;

// sdk#988: the waits of the accessibility probe's speech checks (tests/native/Runic.Desktop.AccessibilityProbe,
// SpeechTimeline.cs), on the phrases and times NVDA's flaky runs logged.
public sealed class AccessibilityProbeSpeechTests
{
    private const string Page = "Runic accessibility fixture";
    private static readonly DateTime Start = new(2026, 11, 14, 22, 19, 27, DateTimeKind.Utc);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(1500);

    private static DateTime At(int milliseconds) => Start.AddMilliseconds(milliseconds);

    [Test]
    public void QuietCountsFromTheLastFocusMoveAsWellAsTheLastPhrase()
    {
        // NVDA named the slider; 850 ms later the focus check moved the focus back to it, and NVDA read the document
        // 80 ms after that. 900 ms after the phrase, the old check (800 ms since the last phrase) let the keys go.
        var timeline = new SpeechTimeline(Start);
        timeline.Heard("Volume slider 7", At(0));
        timeline.Acted(At(850));
        Assert.True(At(900) - At(0) > TimeSpan.FromMilliseconds(800));
        Assert.False(timeline.QuietFor(Quiet, At(900)));
        Assert.False(timeline.QuietFor(TimeSpan.FromMilliseconds(800), At(900)));
        Assert.False(timeline.QuietFor(Quiet, At(850 + 1499)));
        Assert.True(timeline.QuietFor(Quiet, At(850 + 1500)));

        // A phrase after the action counts from the phrase.
        timeline.Heard("Runic accessibility fixture document ￼", At(930));
        Assert.False(timeline.QuietFor(Quiet, At(850 + 1500)));
        Assert.True(timeline.QuietFor(Quiet, At(930 + 1500)));
    }

    [Test]
    public void QuietCountsFromTheStartBeforeAnythingHappens()
    {
        var timeline = new SpeechTimeline(Start);
        Assert.False(timeline.QuietFor(Quiet, At(1000)));
        Assert.True(timeline.QuietFor(Quiet, At(1500)));
    }

    [Test]
    public void TheScreenReaderEndsOnTheControlOnlyWhenItNamedTheControlAfterThePage()
    {
        var timeline = new SpeechTimeline(Start);
        timeline.Heard("Save button", At(0));
        var mark = timeline.Count;
        Assert.False(timeline.EndsOn(mark, "Volume", Page));

        // What NVDA said in run 43646560703: the slider late, on the move away, then the document on the move back.
        timeline.Heard("main landmark", At(100));
        timeline.Heard("Volume slider 7", At(110));
        Assert.True(timeline.EndsOn(mark, "Volume", Page));
        timeline.Heard("Restore points to keep spin button 6 restore points", At(160));
        Assert.True(timeline.EndsOn(mark, "Volume", Page));
        timeline.Heard("Runic accessibility fixture document ￼", At(900));
        Assert.False(timeline.EndsOn(mark, "Volume", Page));

        // Focused once more, NVDA names the slider after the document.
        timeline.Heard("main landmark", At(6000));
        timeline.Heard("Volume slider 7", At(6100));
        Assert.True(timeline.EndsOn(mark, "Volume", Page));
        // Phrases before the mark do not count.
        Assert.False(timeline.EndsOn(timeline.Count, "Volume", Page));
    }

    [Test]
    public void ThePhrasesFromAMarkAreWhatTheCheckHeard()
    {
        var timeline = new SpeechTimeline(Start);
        timeline.Heard("Save button", At(0));
        var mark = timeline.Count;
        timeline.Heard("main landmark", At(100));
        timeline.Heard("Restore points to keep spin button 6 restore points", At(110));
        Assert.Equal("main landmark | Restore points to keep spin button 6 restore points", timeline.Said(mark));
        Assert.Null(timeline.First(mark, phrase => phrase.Contains("Volume", StringComparison.Ordinal)));
        Assert.Equal("main landmark", timeline.First(mark, phrase => phrase.Contains("landmark", StringComparison.Ordinal)));
        Assert.Equal(["Save button", "main landmark", "Restore points to keep spin button 6 restore points"], timeline.Since(0));
    }
}
