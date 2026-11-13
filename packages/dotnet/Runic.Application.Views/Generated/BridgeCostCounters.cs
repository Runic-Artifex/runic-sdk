namespace Runic.Application.Views.Generated;

// What a Bridge's publication did, counted for the scaling suite (tests/dotnet/Runic.Application.Scaling.Tests,
// tests/scaling/budgets.json; D063.2, D064.2). The suite gates on these counts instead of on time. Each count is a plain
// field add in code that already holds the Bridge's model gate, and nothing outside the tests reads them. They are not
// public API.
//
// A1-b (#901, the non-generic Bridge runtime) keeps them as one instance per Bridge (ViewModelBridge.Costs, read through
// IBridgeCostSource) and keeps each increment at the code it counts: the comments name the methods.
internal sealed class BridgeCostCounters
{
    // Units that PublishUnitCore ran.
    internal long UnitsRun;

    // Members a unit encoded to compare them with the published state (WriteChangedMembers), and their bytes (B6).
    internal long MembersSerialized;
    internal long BytesSerialized;

    // Rows whose keys a full scan checked (ScanKeys): a set, a full state, a keyed collection compared as a member, or a
    // collection change without a key baseline.
    internal long KeyScans;

    // Rebuilds of a keyed collection's row-to-index map (TryPublishItem), and the rows they indexed (B2).
    internal long IndexRebuilds;
    internal long IndexRebuildRows;

    // Pending collection changes visited by searches of the pending list: the per-change limit check in
    // TryPublishCollection, MarkCollectionSet's removal, and Commit's field-path loop (B3, B8).
    internal long PendingScans;

    // Frames a unit wrote, and their bytes.
    internal long FramesWritten;
    internal long FrameBytes;
}

// A Bridge's counters, read by the scaling suite without knowing the Bridge's ViewModel type.
internal interface IBridgeCostSource
{
    BridgeCostCounters Costs { get; }

    // How often the data graph was walked from the root (BridgeDataSubscriptions.Walks).
    int DataGraphWalks { get; }
}
