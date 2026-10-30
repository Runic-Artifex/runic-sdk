using System.Globalization;

namespace Runic.Desktop.Internal;

// What an embedded WebView reports while it loads the presentation's first page (WebUiEmbeddedHostOptions.StartupSignal),
// for the connection deadline (I-176).
internal enum WebViewStartupSignal
{
    // A startup step completed, such as the start of the first document request.
    Step,

    // The first navigation started: the WebView is loading the page until the navigation ends.
    NavigationStarted,

    // The first navigation completed.
    NavigationCompleted,

    // The first navigation, or the process that loads it, failed: no more progress is expected from it.
    Failed,
}

// The connection deadline of one presentation, from BeginConnection to its authentication.
//
// A wait gets the connection timeout. Once a request reached the server, it ends a whole timeout after that request, so
// a page that reaches the server just before the deadline can still finish the Bridge handshake (I-180). Before any
// request, an embedded WebView that is still making progress keeps the wait going (I-176): during Windows servicing,
// WebView2 spent 5-25 seconds creating the cookie store of a fresh profile between "navigation started" and its first
// request. While the first navigation is in progress the deadline does not run out, and each startup step allows a
// whole timeout after it, up to CapFactor timeouts from the wait's start; a WebView that hangs still fails. The cap
// scales with the timeout, so a short test timeout keeps a short cap. A browser reports no startup signals, so its
// waits keep the plain rule.
internal sealed class ConnectionDeadline(TimeSpan timeout, TimeProvider time)
{
    // How many connection timeouts a WebView's progress may extend a wait to before any request: 60 s by default.
    internal const int CapFactor = 4;

    private readonly long _origin = time.GetTimestamp();
    private readonly object _gate = new();
    // Since the origin, in TimeSpan ticks; -1 before the first request.
    private long _firstRequest = -1;
    private TimeSpan? _lastStep;
    // While the first navigation is in progress.
    private TimeSpan? _navigationStarted;
    private TaskCompletionSource _changed = NewChanged();
    private int _extensionReported;
    private Expiry? _expired;

    internal TimeSpan Timeout { get; } = timeout;

    // Saturated: a timeout of many days must not overflow.
    internal TimeSpan Cap => Timeout > TimeSpan.MaxValue / CapFactor ? TimeSpan.MaxValue : Timeout * CapFactor;

    internal TimeProvider Time { get; } = time;

    // Since the deadline was created.
    internal TimeSpan Now => Time.GetElapsedTime(_origin);

    internal bool HasRequest => Volatile.Read(ref _firstRequest) >= 0;

    // Completes at the next startup signal, which may move the deadline.
    internal Task Changed
    {
        get
        {
            lock (_gate) return _changed.Task;
        }
    }

    // How the last wait ran out, for the timeout's description; null while none did.
    internal Expiry? Expired
    {
        get
        {
            lock (_gate) return _expired;
        }
    }

    internal void ReportRequest()
    {
        if (Volatile.Read(ref _firstRequest) < 0)
            Interlocked.CompareExchange(ref _firstRequest, Now.Ticks, -1);
    }

    internal void Report(WebViewStartupSignal signal)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            var now = Now;
            switch (signal)
            {
                case WebViewStartupSignal.NavigationStarted:
                    _lastStep = now;
                    _navigationStarted ??= now;
                    break;
                case WebViewStartupSignal.NavigationCompleted:
                    _lastStep = now;
                    _navigationStarted = null;
                    break;
                case WebViewStartupSignal.Failed:
                    _navigationStarted = null;
                    break;
                default:
                    _lastStep = now;
                    break;
            }
            changed = _changed;
            _changed = NewChanged();
        }
        changed.TrySetResult();
    }

    // The time left of a wait that started at started (Now when it began), or null once it ran out, which records
    // Expired. A wait past its timeout that only progress keeps going reports extended once per presentation.
    internal TimeSpan? Remaining(TimeSpan started, out Extension? extended)
    {
        extended = null;
        lock (_gate)
        {
            var now = Now;
            var plain = Add(started, Timeout);
            var deadline = plain;
            var firstRequest = Volatile.Read(ref _firstRequest);
            if (firstRequest >= 0)
            {
                deadline = Max(plain, Add(TimeSpan.FromTicks(firstRequest), Timeout));
            }
            else if (_navigationStarted is not null)
            {
                deadline = Add(started, Cap);
            }
            else if (_lastStep is { } step)
            {
                deadline = Min(Max(plain, Add(step, Timeout)), Add(started, Cap));
            }
            var extending = firstRequest < 0 && deadline > plain;
            if (now >= deadline)
            {
                _expired = new Expiry(now - started, extending ? Describe(now) : null, extending && deadline >= Add(started, Cap));
                return null;
            }
            if (extending && now >= plain && Interlocked.Exchange(ref _extensionReported, 1) == 0)
                extended = new Extension(now - started, Describe(now));
            // An extended wait also wakes at its plain deadline, to report the extension then.
            return (extending && now < plain ? plain : deadline) - now;
        }
    }

    // What kept the wait going; under the gate.
    private string Describe(TimeSpan now) => _navigationStarted is { } navigation
        ? string.Create(CultureInfo.InvariantCulture,
            $"its first navigation started {(now - navigation).TotalSeconds:0.#} seconds earlier and had not ended")
        : string.Create(CultureInfo.InvariantCulture,
            $"its last startup step was {(now - _lastStep.GetValueOrDefault()).TotalSeconds:0.#} seconds earlier");

    private static TimeSpan Add(TimeSpan time, TimeSpan span) => time > TimeSpan.MaxValue - span ? TimeSpan.MaxValue : time + span;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private static TaskCompletionSource NewChanged() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // A wait that ran out after Elapsed. Progress is what extended it, or null when nothing did; AtCap when it ran out
    // at the cap.
    internal sealed record Expiry(TimeSpan Elapsed, string? Progress, bool AtCap);

    // A wait that progress kept going past its timeout, Elapsed into it.
    internal sealed record Extension(TimeSpan Elapsed, string Progress);
}
