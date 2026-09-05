using Runic.Application.Bridge;

namespace RunicDesktopApp;

internal sealed partial class CounterState
{
    private readonly List<int> _history = [0];
    private int _count;

    [BridgeSnapshot]
    private CounterSnapshot GetSnapshot(BridgeSnapshotContext context) => Snapshot(context.CurrentRevision);

    internal CounterSnapshot Increment(int step, long revision)
    {
        _count = checked(_count + step);
        _history.Add(_count);
        return Snapshot(revision);
    }

    internal CounterSnapshot Reset(long revision)
    {
        _count = 0;
        _history.Clear();
        _history.Add(0);
        return Snapshot(revision);
    }

    private CounterSnapshot Snapshot(long revision) => new(_count, _history.ToArray(), revision);
}

internal sealed record CounterSnapshot(int Count, int[] History, [property: BridgeSafeInteger, BridgeMinimum(0)] long Revision);
