using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Runic.Desktop.Internal;

// What the server saw of one presentation's Bridge handshake, with times since
// the launch, so a connection timeout can say where the handshake stopped (#35).
// The first events are kept; repeated rejections are counted instead of listed.
internal sealed class BridgeHandshakeLog
{
    private const int MaximumEvents = 16;

    private readonly object _gate = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    // Formatted when described: formatting the time reads culture data, which loads ICU on every surface's first
    // request otherwise (S1).
    private readonly List<(double Elapsed, string Description)> _events = [];
    private readonly Dictionary<string, int> _rejections = new(StringComparer.Ordinal);
    private int _omitted;

    public void Record(string description)
    {
        var elapsed = Stopwatch.GetElapsedTime(_started).TotalSeconds;
        lock (_gate)
        {
            if (_events.Count < MaximumEvents)
            {
                _events.Add((elapsed, description));
            }
            else
            {
                _omitted++;
            }
        }
    }

    // Lists the first rejection for each reason and counts the rest.
    public void RecordRejection(string reason)
    {
        bool first;
        lock (_gate)
        {
            _rejections.TryGetValue(reason, out var count);
            _rejections[reason] = count + 1;
            first = count == 0;
        }
        if (first)
        {
            Record($"WebSocket rejected: {reason}");
        }
    }

    public string Describe()
    {
        lock (_gate)
        {
            if (_events.Count == 0)
            {
                return "Bridge handshake: nothing recorded.";
            }

            var text = new StringBuilder("Bridge handshake: ");
            text.AppendJoin("; ", _events.Select(static item => string.Create(CultureInfo.InvariantCulture, $"+{item.Elapsed:0.000}s {item.Description}")));
            if (_omitted > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"; {_omitted} later events omitted");
            }
            foreach (var (reason, count) in _rejections)
            {
                if (count > 1)
                {
                    text.Append(CultureInfo.InvariantCulture, $"; {count} WebSockets rejected in total: {reason}");
                }
            }
            return text.Append('.').ToString();
        }
    }
}
