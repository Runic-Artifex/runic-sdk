using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Runic.Application.Views.Generated;

// The publication of a Bridge's state (specs/application/state-changes.md, "Producing frames").
//
// A publication unit compares the encoding of each member other than keyed collections and text members with the
// encoding last published on the route, and publishes every member that differs as a set change, together with the
// pending changes of keyed collections, the text appended to append-only text fields, and the splices of the other text
// members (text-splice/1, or a set when a connection does not use it), as one frame at the window's next revision. A
// unit that changes nothing publishes nothing.
//
// Notifications request a unit, which the delivery runs in a later model turn, so a burst of notifications publishes
// one frame; requested units start at least the window's minimum unit interval apart (BridgeSnapshotDelivery). A
// BridgeSnapshotBatch publishes its unit when the outermost batch ends. A request that changes state publishes its
// unit before it replies (PublishForReply), so a change without a notification is a frame before the reply. A full
// state is published only after the delivery queue overflowed; open and snapshot replies carry one.
//
// Each notification counts as a change (_changes). A unit that compared every member covers the changes counted when
// it started (_publishedChanges): the unit a notification requested is then withdrawn or skipped instead of encoding
// every member again, as after a set, whose reply published the change already. A unit compares each member while it
// is written (BridgeComparingWriter) and keeps the bytes only of members that differ; a member whose value is a scalar
// that equals the value its baseline was written from is not written at all (#911). Pushes are written into pooled
// buffers that the delivery returns once the transport took them; a full state writes its members straight into the
// message. The route's first state, captured at attach, stays as an encoded state until the next change, so an open
// before then copies it instead of encoding every member again.
public partial class ViewModelBridge
{
    // The encoding of each compared member as last published, or null before the route's first state
    // (CaptureFirstState).
    private Dictionary<string, byte[]>? _baselines;
    // The version of each checked field as last published.
    private readonly Dictionary<string, long> _publishedVersions = new(StringComparer.Ordinal);
    // #911: the value that each compared member whose value is a scalar had when its baseline was written, read from the
    // property it encodes (ScalarSources). A unit encodes such a member only when its value differs from this one (or it
    // has none), since equal scalars have equal encodings. Every other member is still encoded in each unit: the
    // comparison is not driven by notifications (specs/application/state-changes.md, "Members").
    private readonly Dictionary<string, object?> _scalarBaselines = new(StringComparer.Ordinal);
    // The scalar values of the members whose encoding changed in the current unit, which become their baselines when its
    // frame is committed.
    private readonly Dictionary<string, object?> _unitScalars = new(StringComparer.Ordinal);
    // The property each state member encodes, in state order, or null when it is not read to compare it as a scalar
    // (ScalarSources, TryReadScalar), and whether it held a scalar value.
    private PropertyDescriptor?[]? _scalarSources;
    private bool[] _scalarTyped = [];
    // Keyed collections whose next publication is a set of the field: reset, replaced, changed in a way indexed changes
    // cannot express, or withheld because of invalid keys.
    private readonly HashSet<BridgeCollectionDescriptor> _collectionSets = [];
    // The instance of each keyed collection the pending changes describe, also by the collection's index in
    // _incrementalCollections, so a collection change finds its collections by reference without reading every getter
    // (#910). Both change only through SetCollectionInstance.
    private readonly Dictionary<BridgeCollectionDescriptor, object?> _collectionInstances = [];
    private object?[]? _instancesByIndex;
    // The keys of each frame-publishing collection as of the last full state and the frames since, so a change that
    // adds a duplicate key is found at once.
    private readonly Dictionary<BridgeCollectionDescriptor, BridgeCollectionKeyBaseline> _collectionKeys = [];
    private readonly List<BridgeCollectionChange> _pendingCollectionChanges = [];
    // The number of pending changes of each keyed collection (by descriptor) that has any, so a change checks the frame's
    // limit without counting the pending list (#910).
    private readonly Dictionary<object, int> _pendingCounts = new(ReferenceEqualityComparer.Instance);
    // The row-to-index map of each frame-publishing collection, for the instance it was built from: a row's own change
    // finds its index without a scan of the rows. A collection change drops it.
    private readonly Dictionary<BridgeCollectionDescriptor, (object Rows, Dictionary<object, int> Indexes)> _collectionIndexes = [];
    // Append-only text fields and text members: the value of each as last published.
    private readonly Dictionary<PropertyDescriptor, string?> _textBaselines = [];
    // Text members: the revision of the last publication that changed each, so a set's splice can name the published
    // value by a revision at or after it (specs/application/state-changes.md, "Text splices").
    private readonly Dictionary<PropertyDescriptor, long> _textChangedRevisions = [];
    // The keys a publication was last withheld for, reported once until a publication succeeds.
    private string? _rejectedKeys;
    // Whether the route's publications are withheld because a keyed collection has invalid keys.
    private bool _withheld;
    // Whether a requested state capture was deferred by a batch.
    private bool _stateDeferred;
    // The revision of the last state or frame published on the route.
    private long _publishedRevision;
    // The notifications counted since the Bridge attached, and the count the last unit that compared every member
    // started with.
    private long _changes;
    private long _publishedChanges = -1;
    // The route's first state as captured at attach: a message holding the encoded "state" object at
    // [_firstStateStart, _firstStateStart + _firstStateLength), current while _firstStateChanges and
    // _firstStateRevision are the route's; released at the first change.
    private BridgeMessage? _firstState;
    private int _firstStateStart;
    private int _firstStateLength;
    private long _firstStateChanges;
    private long _firstStateRevision;
    // The route's latest revision: the last published one, or a later one folded into a requested state or withheld.
    private long _revision;

    private const int MaximumChanges = 4_096;

    // What publication did, for the scaling suite (BridgeCostCounters).
    BridgeCostCounters IBridgeCostSource.Costs => Costs;
    private BridgeCostCounters Costs { get; } = new();

    int IBridgeCostSource.DataGraphWalks => _dataSubscriptions?.Walks ?? 0;

    // Checks every row's key of a keyed collection; counted as rows scanned.
    private BridgeCollectionKeyBaseline ScanKeys(string field, System.Collections.IEnumerable rows, Func<object?, string> keyOf)
    {
        var baseline = BridgeCollectionKeyBaseline.Scan(ModelName, field, rows, keyOf);
        Costs.KeyScans += baseline.Count;
        return baseline;
    }

    /// <summary>Seam (P4 -> P5): publishes everything pending on the route for a reply and returns its revision.</summary>
    /// <remarks>
    /// Call it inside the model turn of the request's effects. It compares every member with the state last published
    /// on the route and publishes the differences, so every state or frame at the returned revision or later includes
    /// the effects. Returns <see langword="null"/> when the route was released or its state is withheld (invalid keys).
    /// </remarks>
    internal long? PublishForReply()
    {
        lock (_modelGate)
        {
            if (IsInactive) return null;
            PublishUnitCore();
            return IsInactive || _withheld ? null : _revision;
        }
    }

    // Seam (P4 -> P5): PublishForReply in a model turn, then waits until the window's pushes requested so far were
    // handed to the transport (bounded by BridgeReplyOrder.ReplyWaitLimit).
    internal async ValueTask<long?> ReplyRevisionAsync(CancellationToken cancellationToken)
    {
        long? revision;
        try { revision = _modelTurn.Run(PublishForReply); }
        catch (ObjectDisposedException) { return null; }
        await WaitForPublishedAsync(cancellationToken).ConfigureAwait(false);
        return revision;
    }

    // Waits until the pushes the window requested so far were handed to the transport, at most ReplyWaitLimit. A
    // request handled inside the model turn cannot wait for a capture that needs that turn, so it does not wait.
    private async ValueTask WaitForPublishedAsync(CancellationToken cancellationToken)
    {
        if (_modelTurn.IsCurrent || _replyOrder.WhenPublished() is not { } published) return;
        await _replyOrder.WaitAsync(published, _name, cancellationToken).ConfigureAwait(false);
    }

    // A change: requests a publication unit, which a batch defers to its end. Before the route's first state, the unit
    // captures that state.
    private void ScheduleUnit() => _modelTurn.TryRunNotification(() =>
    {
        lock (_modelGate)
        {
            if (IsInactive) return;
            _changes++;
            if (BridgeSnapshotBatch.TryDefer(_vm, this)) return;
            _delivery.RequestUnit();
        }
    });

    void IBridgeSnapshotBatchParticipant.FlushSnapshotBatch() => _modelTurn.TryRunNotification(() =>
    {
        lock (_modelGate) PublishUnitCore();
    });

    // Called by the delivery in a model turn.
    private void RunRequestedUnit()
    {
        lock (_modelGate)
        {
            if (IsInactive) return;
            _dataSubscriptions?.SweepPending();
            // A batch still open publishes its unit when it ends.
            if (BridgeSnapshotBatch.TryDefer(_vm, this)) return;
            // Nothing was notified since a unit compared every member, as the reply of a set did, and no renewal waits.
            if (_baselines is not null && !_stateDeferred && _publishedChanges == _changes && !RenewalPending) return;
            PublishUnitCore();
        }
    }

    private long NextRevision() => _content?.NextRevision() ?? _revision + 1;

    // Publishes one unit; the model gate is held, in a model turn.
    private void PublishUnitCore()
    {
        if (IsInactive) return;
        Costs.UnitsRun++;
        _dataSubscriptions?.SweepPending();
        RefreshPendingMembers();
        var changed = _changes;
        if (_baselines is null)
        {
            if (CaptureFirstState()) Published(changed);
            return;
        }
        if (_firstState is not null && (_firstStateChanges != changed || _firstStateRevision != _revision)) ReleaseFirstState();
        // A renewal (presentation-epoch/1) is a full state at a new revision, also when nothing changed.
        if (RenewalPending)
        {
            PublishRenewal(changed);
            return;
        }
        if (_stateDeferred)
        {
            _stateDeferred = false;
            _delivery.RequestState();
        }
        if (_delivery.StateRequested)
        {
            // The requested state shows this unit, at the revision current when it is captured.
            _revision = NextRevision();
            ClearPendingChanges();
            _collectionSets.Clear();
            Published(changed);
            return;
        }
        List<Change> changes;
        List<(string Name, byte[] Value)> members;
        try
        {
            members = WriteChangedMembers();
            changes = Compare(members);
        }
        catch (BridgeSnapshotDetachedException) { return; }
        catch (BridgeCollectionKeyException keys)
        {
            Withhold(keys);
            Published(changed);
            return;
        }
        if (changes.Count == 0)
        {
            Published(changed);
            return;
        }
        var revision = NextRevision();
        _revision = revision;
        Published(changed);
        var baseRevision = _publishedRevision;
        var frame = WriteFrame(changes, baseRevision, revision, withSplices: true);
        Costs.FramesWritten++;
        Costs.FrameBytes += frame.Length;
        // A connection that does not use text-splice/1 may complete its hello before the frame is broadcast.
        Func<byte[]>? fallback = changes.Exists(static change => change.IsSplice)
            ? () => WriteValue(writer => WriteFrameTo(writer, changes, baseRevision, revision, withSplices: false)) : null;
        if (!_delivery.EnqueueFrame(frame, fallback))
        {
            PublishRecoveryState();
            return;
        }
        Commit(members, revision);
        ReleaseFirstState();
    }

    // A unit compared every member as of changed notifications: the unit a later notification requested is not
    // needed unless one arrived meanwhile.
    private void Published(long changed)
    {
        _publishedChanges = changed;
        if (_changes == changed) _delivery.WithdrawUnit();
    }

    // The changes of a unit against the published baselines; throws BridgeCollectionKeyException when a keyed
    // collection that becomes a set has invalid keys.
    private List<Change> Compare(List<(string Name, byte[] Value)> members)
    {
        var changes = new List<Change>();
        // Keyed collections: their pending changes, or a set of the field.
        var indexed = new Dictionary<BridgeCollectionDescriptor, List<BridgeCollectionChange>>();
        foreach (var change in _pendingCollectionChanges)
        {
            if (_collectionSets.Contains(change.Descriptor)) continue;
            if (!indexed.TryGetValue(change.Descriptor, out var list)) indexed.Add(change.Descriptor, list = []);
            list.Add(change);
        }
        var sets = new List<BridgeCollectionDescriptor>(_collectionSets);
        var count = indexed.Values.Sum(list => list.Count) + sets.Count;
        var memberSets = new List<Change>(members.Count);
        foreach (var (name, value) in members) memberSets.Add(Change.Set(name, value, VersionOf(name)));
        var texts = new List<Change>();
        foreach (var field in _textFields)
        {
            var value = field.Get(_vm) as string;
            _textBaselines.TryGetValue(field, out var baseline);
            if (string.Equals(value, baseline, StringComparison.Ordinal)) continue;
            var name = field.AppendOnlyTextField!;
            if (value is null || baseline is null || BridgeTextDelta.Between(baseline, value) is not { } delta)
                texts.Add(Change.Set(name, WriteValue(writer => { if (value is null) writer.WriteNullValue(); else writer.WriteStringValue(value); }), null));
            else texts.Add(Change.Text(name, delta, value));
        }
        var splices = BridgeDispatcher.For(_transport).TextSpliceInUse;
        foreach (var member in _textMembers)
        {
            var value = member.Get(_vm) as string;
            _textBaselines.TryGetValue(member, out var baseline);
            var version = VersionOf(member.WireName);
            var versionChanged = version is not null && _publishedVersions.GetValueOrDefault(member.WireName) != version;
            if (string.Equals(value, baseline, StringComparison.Ordinal) && !versionChanged) continue;
            BridgeTextSplice first = default, second = default;
            var spliceCount = value is null || baseline is null || !splices || string.Equals(value, baseline, StringComparison.Ordinal)
                ? 0 : BridgeTextSplice.Between(baseline, value, out first, out second);
            if (spliceCount == 0)
            {
                texts.Add(Change.Set(member.WireName, WriteMember(member.WireName), version));
                continue;
            }
            texts.Add(Change.Splice(member.WireName, baseline!.Length, first, version, value!));
            if (spliceCount == 2) texts.Add(Change.Splice(member.WireName, baseline.Length - first.Delete, second, version, value!));
        }
        count += memberSets.Count + texts.Count;
        // A frame carries at most 4,096 changes: the indexed changes of the collection with the most become a set.
        while (count > MaximumChanges && indexed.Count > 0)
        {
            var largest = indexed.MaxBy(pair => pair.Value.Count);
            indexed.Remove(largest.Key);
            sets.Add(largest.Key);
            count -= largest.Value.Count - 1;
        }
        foreach (var descriptor in _incrementalCollections)
        {
            if (sets.Contains(descriptor))
            {
                var rows = descriptor.Get(_vm);
                // A set validates the keys as a full state does.
                if (rows is System.Collections.IEnumerable enumerable)
                    _collectionKeys[descriptor] = ScanKeys(descriptor.Name, enumerable, descriptor.Key);
                else _collectionKeys.Remove(descriptor);
                changes.Add(Change.Set(descriptor.Name, WriteMember(descriptor.Name), null));
            }
            else if (indexed.TryGetValue(descriptor, out var list))
                foreach (var change in list) changes.Add(Change.Indexed(change));
        }
        // A keyed collection compared as a member (of a View with validation) is checked like a full state.
        foreach (var change in memberSets)
            if (Array.Find(_keyedCollections, descriptor => descriptor.Name == change.Field) is { } keyed
                && keyed.Get(_vm) is System.Collections.IEnumerable rows)
                ScanKeys(keyed.Name, rows, keyed.Key);
        changes.AddRange(memberSets);
        changes.AddRange(texts);
        return changes;
    }

    private long? VersionOf(string member) =>
        _checkedByMember.TryGetValue(member, out var field) ? field.Registry.Current.Version : null;

    // The baselines after a published frame: members holds the members that changed.
    private void Commit(List<(string Name, byte[] Value)> members, long revision, bool fullState = false)
    {
        // field-paths/1: the paths that this publication changed take its revision as their version. Indexed changes
        // carry their rows' encodings, so only those rows are observed; a set or a full state scans the member.
        foreach (var target in _pathTargets.Values)
        {
            if (fullState || members.Exists(member => member.Name == target.Member)
                || target.Rows is { } set && _collectionSets.Contains(set))
            {
                _fieldPaths.Scan(target.Member, FieldPathContainers(target), target.Segments, revision);
                continue;
            }
            if (target.Rows is not { } rows) continue;
            Costs.PendingScans += _pendingCollectionChanges.Count;
            foreach (var change in _pendingCollectionChanges)
            {
                if (change.Descriptor != rows) continue;
                if (change.Kind is "remove" or "replace")
                    foreach (var key in change.Keys)
                        if (Array.IndexOf(change.ItemKeys, key) < 0) _fieldPaths.Remove(target.Member, key);
                for (var index = 0; index < change.ItemKeys.Length && index < change.Items.Length; index++)
                    _fieldPaths.Observe(target.Member, change.ItemKeys[index], change.Items[index], target.Segments, revision);
            }
        }
        foreach (var (name, value) in members) _baselines![name] = value;
        if (!fullState)
            foreach (var (name, value) in _unitScalars) _scalarBaselines[name] = value;
        _unitScalars.Clear();
        foreach (var field in _checkedProperties) _publishedVersions[field.Descriptor.WireName] = field.Registry.Current.Version;
        foreach (var field in _textFields) _textBaselines[field] = field.Get(_vm) as string;
        foreach (var member in _textMembers)
        {
            var value = member.Get(_vm) as string;
            if (!_textBaselines.TryGetValue(member, out var baseline) || !string.Equals(baseline, value, StringComparison.Ordinal))
                _textChangedRevisions[member] = revision;
            _textBaselines[member] = value;
        }
        _publishedRevision = revision;
        ClearPendingChanges();
        _collectionSets.Clear();
        _withheld = false;
        _rejectedKeys = null;
    }

    // Keys are invalid: nothing is published until they are valid again. The unit takes a revision, the client is told
    // why, and the collections with pending changes become sets of their fields.
    private void Withhold(BridgeCollectionKeyException keys)
    {
        _withheld = true;
        _revision = NextRevision();
        foreach (var change in _pendingCollectionChanges) _collectionSets.Add(change.Descriptor);
        ClearPendingChanges();
        foreach (var descriptor in _keyedCollections.Where(descriptor => descriptor.Name == keys.Field && descriptor.PublishesChanges))
            _collectionSets.Add(descriptor);
        ReportRejectedKeys(keys);
    }

    // A recovery state follows frames the host has not taken. It is captured at once, so later frames can queue behind
    // it, unless a captured state is already queued: then the queued state becomes a requested one, captured when the
    // transport takes it, instead of serializing another full state per change.
    private void PublishRecoveryState()
    {
        if (BridgeTelemetry.RecoverySnapshots.Enabled)
            BridgeTelemetry.RecoverySnapshots.Add(1, new KeyValuePair<string, object?>(BridgeTelemetry.ModelTag, ModelName));
        if (_delivery.StateQueued)
        {
            ClearPendingChanges();
            _collectionSets.Clear();
            _delivery.RequestState();
            return;
        }
        BridgeMessage? state;
        // This runs in a model turn of whatever changed the route. A state that cannot be captured now (for example
        // because of invalid keys) becomes a request, which the delivery captures and reports.
        try { state = WriteStatePush(); }
        catch (Exception) when (!IsInactive)
        {
            ClearPendingChanges();
            _collectionSets.Clear();
            _delivery.RequestState();
            return;
        }
        if (state is null) return;
        if (IsInactive) state.Release();
        else _delivery.EnqueueState(state);
    }

    // The route's first state, captured in the first unit after the Bridge attached, whether or not a page is
    // connected. Writing it presents the content of the View's content slots, which binds that content to the window's
    // model context, so what the window presents follows the model rather than when a page first reads it. Nothing is
    // pushed: it is the baseline of the route's frames, and an open or snapshot reads the state at its revision; an
    // open copies this one until the route changes. While a keyed collection has invalid keys there is no first state; the
    // next unit tries again, and a read reports them. Returns whether it captured the state.
    private bool CaptureFirstState()
    {
        var message = BridgePublicationWriter.Rent();
        try
        {
            message.Writer.WriteStartObject();
            var (start, length) = WriteState(message, updateBaselines: true);
            message.Writer.WriteEndObject();
            ReleaseFirstState();
            _firstState = message.Detach();
            _firstStateStart = start;
            _firstStateLength = length;
            _firstStateChanges = _changes;
            _firstStateRevision = _revision;
            // The first state begins the route's first epoch: a renewal requested before it has nothing to renew.
            Volatile.Write(ref _renewal, 0);
            return true;
        }
        catch (BridgeSnapshotDetachedException) { return false; }
        catch (BridgeCollectionKeyException) { return false; }
        finally { message.Dispose(); }
    }

    // Whether the first state is still the route's state: nothing was notified or published since it was captured.
    private bool FirstStateIsCurrent => _firstState is not null && _firstStateChanges == _changes
        && _firstStateRevision == _revision && !_stateDeferred && _pendingCollectionChanges.Count == 0
        && _collectionSets.Count == 0 && !_delivery.StateRequested && !RenewalPending;

    // Writes "revision", "epoch" (when asked), "state" and "fields" of the first state into an open object.
    private void WriteFirstState(Utf8JsonWriter writer, bool epoch)
    {
        writer.WriteNumber("revision", _revision);
        if (epoch) writer.WriteNumber("epoch", _epoch);
        writer.WritePropertyName("state");
        writer.WriteRawValue(_firstState!.Memory.Span.Slice(_firstStateStart, _firstStateLength), skipInputValidation: true);
        WriteFieldVersions(writer);
    }

    private void ReleaseFirstState()
    {
        _firstState?.Release();
        _firstState = null;
    }

    // Called by the delivery in a model turn after it took the request.
    private BridgeMessage? CaptureRequestedState()
    {
        lock (_modelGate)
        {
            if (IsInactive) return null;
            _dataSubscriptions?.SweepPending();
            // Do not show the host a half-applied batch: its end requests the state again.
            if (BridgeSnapshotBatch.TryDefer(_vm, this))
            {
                _stateDeferred = true;
                return null;
            }
            try { return WriteStatePush(); }
            catch (BridgeCollectionKeyException keys)
            {
                // The client keeps the last published state; the next publication with valid keys sets the
                // collections.
                if (!IsInactive) Withhold(keys);
                return null;
            }
        }
    }

    private BridgeMessage? WriteStatePush()
    {
        var message = BridgePublicationWriter.Rent();
        try
        {
            var writer = message.Writer;
            writer.WriteStartObject();
            writer.WriteString("kind", "state");
            writer.WriteString("route", _name);
            // A broadcast: it carries epoch while a connection uses presentation-epoch/1, and others ignore it.
            WriteState(message, updateBaselines: true, epoch: BridgeDispatcher.For(_transport).PresentationEpochInUse);
            writer.WriteEndObject();
            return message.Detach();
        }
        // The session detached this route meanwhile; there is nothing to publish.
        catch (BridgeSnapshotDetachedException) { return null; }
        finally { message.Dispose(); }
    }

    // Writes "revision", "epoch" (when asked), "state" and "fields" of the route's full state into an open object of
    // message, at the route's latest revision, each member straight into the message. With updateBaselines, the state becomes the baseline of
    // the next frame (a published state, or a reply's state, which equals the published one), and a requested unit
    // that it covers is withdrawn. Returns where the
    // "state" object is in the message. Throws BridgeCollectionKeyException for invalid keys.
    private (int Start, int Length) WriteState(BridgePublicationWriter message, bool updateBaselines, bool epoch = false)
    {
        RefreshPendingMembers();
        var changed = _changes;
        var writer = message.Writer;
        List<(string Name, long Start, long End)>? compared = updateBaselines ? new(_stateMembers.Length) : null;
        long stateStart, stateEnd;
        using (BridgeSnapshotPublication.Enter(() => IsInactive, _logger, _content?.Validation))
        {
            CheckCollectionKeys();
            writer.WriteNumber("revision", _revision);
            if (epoch) writer.WriteNumber("epoch", _epoch);
            writer.WritePropertyName("state");
            stateStart = message.Position;
            writer.WriteStartObject();
            foreach (var member in _stateMembers)
            {
                writer.WritePropertyName(member.Name);
                if (compared is null || _incrementalMembers.Contains(member.Name))
                {
                    WriteMemberValue(member, writer);
                    continue;
                }
                var start = message.Position;
                WriteMemberValue(member, writer);
                compared.Add((member.Name, start, message.Position));
            }
            writer.WriteEndObject();
            stateEnd = message.Position;
        }
        BridgeSnapshotPublication.ThrowIfInactive();
        if (IsInactive) throw new BridgeSnapshotDetachedException();
        WriteFieldVersions(writer);
        var state = (checked((int)stateStart), checked((int)(stateEnd - stateStart)));
        if (!updateBaselines) return state;
        _baselines ??= new(StringComparer.Ordinal);
        // An unchanged member keeps its baseline array.
        foreach (var (name, start, end) in compared!)
        {
            var length = (int)(end - start);
            if (_baselines.TryGetValue(name, out var previous) && previous.Length == length
                && message.Buffer.SequenceEqual(start, previous)) continue;
            _baselines[name] = message.Buffer.ToArray(start, length);
        }
        // The values the state's scalar members were written from.
        _scalarBaselines.Clear();
        for (var index = 0; index < _stateMembers.Length; index++)
            if (_baselines.TryGetValue(_stateMembers[index].Name, out var encoding) && TryReadScalar(index, encoding, out var value))
                _scalarBaselines[_stateMembers[index].Name] = value;
        foreach (var descriptor in _incrementalCollections) SetCollectionInstance(descriptor, descriptor.Get(_vm));
        Commit([], _revision, fullState: true);
        // The state is a unit that compared every member.
        Published(changed);
        return state;
    }

    // blobs/1: a state member's blobs are exactly those of its latest write (BridgeBlobs, holders). A keyed collection
    // that publishes its changes holds each row's blobs by the row's key instead, so a row change replaces only that
    // row's blobs; its rows are written as its changes write them, which is the member's own encoding.
    private void WriteMemberValue(BridgeStateMember member, Utf8JsonWriter writer)
    {
        using var route = EnterBlobs(null);
        if (IncrementalCollection(member.Name) is { } descriptor && descriptor.Get(_vm) is System.Collections.IEnumerable rows)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                var key = descriptor.Key(row);
                keys.Add(key);
                using var slot = BridgeBlobScope.EnterSlot(RowSlot(descriptor, key));
                descriptor.WriteItem(writer, row);
            }
            writer.WriteEndArray();
            Blobs.ReleaseSlots(this, RowSlot(descriptor, ""), keep: slot => keys.Contains(slot[(descriptor.Name.Length + 1)..]));
            return;
        }
        using (BridgeBlobScope.EnterSlot(member.Name)) member.Write(writer, _vm);
    }

    // A collection change's row: its blobs are those of the row's latest write, by its key.
    private void WriteItemValue(BridgeCollectionDescriptor descriptor, Utf8JsonWriter writer, object? item)
    {
        using var route = EnterBlobs(null);
        using var slot = BridgeBlobScope.EnterSlot(RowSlot(descriptor, descriptor.Key(item)));
        descriptor.WriteItem(writer, item);
    }

    // The rows a collection change took away release their blobs.
    private void ReleaseRows(BridgeCollectionDescriptor descriptor, IEnumerable<string> keys)
    {
        foreach (var key in keys) Blobs.ReleaseSlots(this, RowSlot(descriptor, key), exact: true);
    }

    private static string RowSlot(BridgeCollectionDescriptor descriptor, string key) => descriptor.Name + "\u0000" + key;

    private BridgeCollectionDescriptor? IncrementalCollection(string name)
    {
        foreach (var descriptor in _incrementalCollections)
            if (descriptor.Name == name) return descriptor;
        return null;
    }

    private void WriteFieldVersions(Utf8JsonWriter writer)
    {
        if (_checkedProperties.Length == 0) return;
        writer.WriteStartObject("fields");
        foreach (var field in _checkedProperties)
        {
            writer.WriteStartObject(field.Descriptor.WireName);
            writer.WriteNumber("version", field.Registry.Current.Version);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    // The compared members (all but keyed collections and append-only text) whose encoding or checked version differs
    // from what the route published, with their new encoding. An unchanged member is compared while it is written and
    // allocates nothing; an unchanged scalar is compared by its value and not written (#911).
    private List<(string Name, byte[] Value)> WriteChangedMembers()
    {
        var changed = new List<(string Name, byte[] Value)>();
        _unitScalars.Clear();
        using (BridgeSnapshotPublication.Enter(() => IsInactive, _logger, _content?.Validation))
        {
            for (var index = 0; index < _stateMembers.Length; index++)
            {
                var member = _stateMembers[index];
                if (_incrementalMembers.Contains(member.Name)) continue;
                _baselines!.TryGetValue(member.Name, out var previous);
                var version = VersionOf(member.Name);
                var isScalar = TryReadScalar(index, previous, out var scalar);
                if (isScalar)
                {
                    if (previous is not null && _scalarBaselines.TryGetValue(member.Name, out var written) && SameScalar(written, scalar))
                    {
                        if (version is not null && _publishedVersions.GetValueOrDefault(member.Name) != version) changed.Add((member.Name, previous));
                        continue;
                    }
                }
                // Until a publication writes the member, it has no scalar baseline.
                _scalarBaselines.Remove(member.Name);
                var comparing = BridgeComparingWriter.Rent(previous);
                try
                {
                    WriteMemberValue(member, comparing.Writer);
                    Costs.MembersSerialized++;
                    Costs.BytesSerialized += comparing.Writer.BytesCommitted + comparing.Writer.BytesPending;
                    var matches = comparing.Matches;
                    if (isScalar)
                    {
                        // The value read before the member was written is the value of the encoding compared.
                        if (matches) _scalarBaselines[member.Name] = scalar;
                        else _unitScalars[member.Name] = scalar;
                    }
                    if (matches && (version is null || _publishedVersions.GetValueOrDefault(member.Name) == version)) continue;
                    changed.Add((member.Name, matches ? previous! : comparing.ToArray()));
                }
                finally { comparing.Dispose(); }
            }
        }
        BridgeSnapshotPublication.ThrowIfInactive();
        if (IsInactive) throw new BridgeSnapshotDetachedException();
        return changed;
    }

    // The property whose value each state member encodes, in state order: the one property with the member's wire name,
    // unless the member publishes changes (keyed collections, text) or reads an observed sub-object. Null otherwise.
    private PropertyDescriptor?[] ScalarSources()
    {
        var sources = new PropertyDescriptor?[_stateMembers.Length];
        _scalarTyped = new bool[_stateMembers.Length];
        for (var index = 0; index < sources.Length; index++)
        {
            var name = _stateMembers[index].Name;
            if (_incrementalMembers.Contains(name)) continue;
            PropertyDescriptor? source = null;
            var count = 0;
            foreach (var property in _properties)
            {
                if (!string.Equals(property.WireName, name, StringComparison.Ordinal)) continue;
                source = property;
                count++;
            }
            if (count == 1 && source!.Observe is null && source.QueryRowWriter is null) sources[index] = source;
        }
        return sources;
    }

    // Reads the value of the compared member at index in state order to compare it as a scalar, or returns false without
    // reading it. encoding is the member's encoding as last written, or null. A member is read once it held a scalar, or
    // while its encoding is a number, string or Boolean; a member whose encoding is an object or an array, or whose value
    // is no scalar, is not read for this again, so the comparison adds no getter call for it.
    private bool TryReadScalar(int index, ReadOnlySpan<byte> encoding, out object? value)
    {
        value = null;
        var sources = _scalarSources ??= ScalarSources();
        if (sources[index] is not { } source) return false;
        if (!_scalarTyped[index])
        {
            if (encoding.IsEmpty || encoding[0] == (byte)'n') return false;
            if (encoding[0] is (byte)'{' or (byte)'[')
            {
                sources[index] = null;
                return false;
            }
        }
        value = source.Get(_vm);
        // A null value of a member that held a scalar encodes as null whatever its type.
        if (value is null) return _scalarTyped[index];
        if (!IsScalar(value))
        {
            sources[index] = null;
            value = null;
            return false;
        }
        _scalarTyped[index] = true;
        return true;
    }

    // Whether a value's encoding is a function of the value alone, compared by SameScalar: a string or an immutable
    // primitive, enum, date or time value. Decimal is not, as 1.0m and 1.00m are equal but encode differently.
    private static bool IsScalar(object value) => value is string or bool or char or byte or sbyte or short or ushort
        or int or uint or long or ulong or float or double or Guid or DateTime or DateTimeOffset or TimeSpan or DateOnly or TimeOnly
        or Enum;

    // Whether two scalars have the same encoding: equal, of the same type, with floating-point values compared by their
    // bits (0.0 and -0.0) and dates by their kind or offset as well.
    private static bool SameScalar(object? written, object? value) => (written, value) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (string left, string right) => string.Equals(left, right, StringComparison.Ordinal),
        (double left, double right) => BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right),
        (float left, float right) => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right),
        (DateTime left, DateTime right) => left.Ticks == right.Ticks && left.Kind == right.Kind,
        (DateTimeOffset left, DateTimeOffset right) => left.EqualsExact(right),
        _ => written.GetType() == value.GetType() && written.Equals(value),
    };

    // The encoding of one member.
    private byte[] WriteMember(string name)
    {
        using (BridgeSnapshotPublication.Enter(() => IsInactive, _logger, _content?.Validation))
            foreach (var member in _stateMembers)
                if (member.Name == name) return WriteValue(writer => WriteMemberValue(member, writer));
        throw new InvalidOperationException($"{ModelName} has no state member '{name}'.");
    }

    private BridgeMessage WriteFrame(List<Change> changes, long baseRevision, long revision, bool withSplices) =>
        WritePush(writer => WriteFrameTo(writer, changes, baseRevision, revision, withSplices));

    // Without splices, each spliced text member is one set of its value after the frame.
    private void WriteFrameTo(Utf8JsonWriter writer, List<Change> changes, long baseRevision, long revision, bool withSplices)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "frame");
        writer.WriteString("route", _name);
        writer.WriteNumber("baseRevision", baseRevision);
        writer.WriteNumber("revision", revision);
        writer.WriteStartArray("changes");
        string? spliced = null;
        foreach (var change in changes)
        {
            if (withSplices || !change.IsSplice) change.Write(writer);
            else if (!string.Equals(spliced, change.Field, StringComparison.Ordinal))
            {
                spliced = change.Field;
                change.WriteAsSet(writer);
            }
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // A set request's splice names its base by a revision: the member's value in the route's state at that revision is
    // the value last published, when no publication changed the member after the revision and the revision is not
    // beyond the last publication. Null when the producer does not have that value or it is not text.
    private string? PublishedText(PropertyDescriptor member, long revision) =>
        revision <= _publishedRevision && revision >= _textChangedRevisions.GetValueOrDefault(member)
            && _textBaselines.TryGetValue(member, out var value) ? value : null;

    // A keyed collection's next publication is a set of the field.
    private void MarkCollectionSet(BridgeCollectionDescriptor descriptor)
    {
        if (_baselines is null) return;
        _collectionSets.Add(descriptor);
        // Once per collection and unit at most: changes of a collection that becomes a set are not kept.
        if (_pendingCounts.Remove(descriptor))
        {
            Costs.PendingScans += _pendingCollectionChanges.Count;
            _pendingCollectionChanges.RemoveAll(change => change.Descriptor == descriptor);
        }
        _collectionKeys.Remove(descriptor);
    }

    private void AddPendingChange(BridgeCollectionChange change)
    {
        _pendingCollectionChanges.Add(change);
        _pendingCounts[change.Descriptor] = _pendingCounts.GetValueOrDefault(change.Descriptor) + 1;
    }

    private void ClearPendingChanges()
    {
        _pendingCollectionChanges.Clear();
        _pendingCounts.Clear();
    }

    // Makes rows the instance whose changes the pending changes of descriptor describe.
    private void SetCollectionInstance(BridgeCollectionDescriptor descriptor, object? rows)
    {
        _collectionInstances[descriptor] = rows;
        _instancesByIndex ??= new object?[_incrementalCollections.Length];
        _instancesByIndex[Array.IndexOf(_incrementalCollections, descriptor)] = rows;
    }

    // The frame-publishing keyed collections whose getter returns collection, in declaration order: those whose instance
    // it is, found by reference and confirmed with one getter read each (#910). After a named notification, until the
    // next unit refreshes every member, a getter may return an instance the Bridge has not read yet (#891), so a
    // collection that is no confirmed collection's instance is then looked for in every getter.
    private BridgeCollectionDescriptor[] CollectionsHolding(INotifyCollectionChanged collection)
    {
        var instances = _instancesByIndex ?? [];
        BridgeCollectionDescriptor[]? holding = null;
        for (var index = 0; index < instances.Length; index++)
        {
            if (!ReferenceEquals(instances[index], collection)) continue;
            var descriptor = _incrementalCollections[index];
            if (ReferenceEquals(descriptor.Get(_vm), collection)) holding = holding is null ? [descriptor] : [.. holding, descriptor];
        }
        if (holding is not null) return holding;
        if (!_memberRefreshPending) return [];
        return _incrementalCollections.Where(descriptor => ReferenceEquals(descriptor.Get(_vm), collection)).ToArray();
    }

    // A closed model context makes the change one for a released Bridge: handled, nothing published.
    private bool TryPublishCollection(INotifyCollectionChanged collection, NotifyCollectionChangedEventArgs args)
    {
        return _modelTurn.RunNotification(() =>
        {
            lock (_modelGate)
            {
                if (IsInactive) return true;
                var descriptors = CollectionsHolding(collection);
                if (descriptors.Length == 0) return false;
                foreach (var descriptor in descriptors) _collectionIndexes.Remove(descriptor);
                if (_baselines is null) return true;
                var kind = args.Action switch
                {
                    NotifyCollectionChangedAction.Add => "add",
                    NotifyCollectionChangedAction.Remove => "remove",
                    NotifyCollectionChangedAction.Replace => "replace",
                    NotifyCollectionChangedAction.Move => "move",
                    _ => "reset"
                };
                var indexed = !(kind == "reset" || args.NewStartingIndex < 0 && kind is "add" or "replace" or "move" ||
                    args.OldStartingIndex < 0 && kind is "remove" or "replace" or "move"
                    || kind == "replace" && (args.NewItems?.Count ?? 0) != (args.OldItems?.Count ?? 0));
                foreach (var descriptor in descriptors)
                {
                    if (_collectionSets.Contains(descriptor) || ReplacedUnnoticed(descriptor, collection)) continue;
                    // A change without a position, or with invalid keys, becomes a set of the field; the unit checks
                    // the keys and withholds the publication while they are invalid.
                    if (!indexed || !TrackCollectionKeys(descriptor, collection, args))
                    {
                        MarkCollectionSet(descriptor);
                        continue;
                    }
                    var keys = (kind == "add" ? args.NewItems : args.OldItems)?.Cast<object?>().Select(descriptor.Key).ToArray() ?? [];
                    var items = kind is "add" or "replace"
                        ? args.NewItems?.Cast<object?>().Select(item => WriteValue(writer => WriteItemValue(descriptor, writer, item))).ToArray() ?? []
                        : [];
                    // blobs/1: a removed row, or a replaced one whose key no new row has, releases its blobs.
                    if (kind is "remove" or "replace")
                    {
                        var kept = kind == "replace"
                            ? args.NewItems?.Cast<object?>().Select(descriptor.Key).ToHashSet(StringComparer.Ordinal) ?? []
                            : [];
                        ReleaseRows(descriptor, keys.Where(key => !kept.Contains(key)));
                    }
                    AddPendingChange(new(descriptor, kind,
                        kind == "remove" ? args.OldStartingIndex : args.NewStartingIndex,
                        args.OldStartingIndex, keys, items)
                    {
                        ItemKeys = kind == "replace" ? args.NewItems?.Cast<object?>().Select(descriptor.Key).ToArray() ?? [] : kind == "add" ? keys : [],
                    });
                    if (_pendingCounts[descriptor] > MaximumChanges) MarkCollectionSet(descriptor);
                }
                ScheduleUnit();
                return true;
            }
        }, closed: true);
    }

    private bool TryPublishItem(object item)
    {
        return _modelTurn.RunNotification(() =>
        {
            lock (_modelGate)
            {
                if (IsInactive) return true;
                var handled = false;
                foreach (var descriptor in _incrementalCollections)
                {
                    if (descriptor.Get(_vm) is not System.Collections.IList rows) continue;
                    // A getter that returns another instance, whether or not a notification said so, has another index.
                    if (!_collectionIndexes.TryGetValue(descriptor, out var map) || !ReferenceEquals(map.Rows, rows))
                    {
                        var built = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
                        Costs.IndexRebuilds++;
                        Costs.IndexRebuildRows += rows.Count;
                        for (var index = 0; index < rows.Count; index++)
                            if (rows[index] is { } row) built[row] = index;
                        _collectionIndexes[descriptor] = map = (rows, built);
                    }
                    var indexes = map.Indexes;
                    if (!indexes.TryGetValue(item, out var at) || at >= rows.Count || !ReferenceEquals(rows[at], item)) continue;
                    handled = true;
                    if (_baselines is null || _collectionSets.Contains(descriptor) || ReplacedUnnoticed(descriptor, rows)) continue;
                    // A key changed in place or made invalid becomes a set of the field.
                    if (!TrackItemKey(descriptor, rows, item, at, out var key)) MarkCollectionSet(descriptor);
                    else AddPendingChange(new(descriptor, "replace", at, at,
                        [key], [WriteValue(writer => WriteItemValue(descriptor, writer, item))]) { ItemKeys = [key] });
                }
                if (handled) ScheduleUnit();
                return handled;
            }
        }, closed: true);
    }

    // A change of a keyed collection instance that no notification reported yet, such as one a getter returns after
    // another member's notification, cannot be described against the previous instance: the field becomes a set.
    private bool ReplacedUnnoticed(BridgeCollectionDescriptor descriptor, object rows)
    {
        if (_collectionInstances.TryGetValue(descriptor, out var previous) && ReferenceEquals(previous, rows)) return false;
        SetCollectionInstance(descriptor, rows);
        _collectionIndexes.Remove(descriptor);
        MarkCollectionSet(descriptor);
        return true;
    }

    // Checks every keyed collection's keys and makes them the baseline for frames: a state whose keyed collection has a
    // null row or a null, empty or duplicate key is never written (BridgeCollectionKeyException).
    private void CheckCollectionKeys()
    {
        foreach (var descriptor in _keyedCollections)
        {
            _collectionKeys.Remove(descriptor);
            if (descriptor.Get(_vm) is not System.Collections.IEnumerable rows) continue;
            var baseline = ScanKeys(descriptor.Name, rows, descriptor.Key);
            if (descriptor.PublishesChanges) _collectionKeys[descriptor] = baseline;
        }
    }

    // Updates the key baseline for one indexed collection notification and returns whether a frame may describe it. It
    // never throws into the code that changed the collection.
    private bool TrackCollectionKeys(BridgeCollectionDescriptor descriptor, object rows, NotifyCollectionChangedEventArgs args)
    {
        var enumerable = (System.Collections.IEnumerable)rows;
        try
        {
            if (!_collectionKeys.TryGetValue(descriptor, out var tracked) || !ReferenceEquals(tracked.Rows, rows))
            {
                _collectionKeys[descriptor] = ScanKeys(descriptor.Name, enumerable, descriptor.Key);
                return true;
            }
            if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace && args.OldItems is { } removed)
                foreach (var row in removed) tracked.Remove(row, descriptor.Key);
            if (args.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace && args.NewItems is { } added)
            {
                var index = args.NewStartingIndex;
                foreach (var row in added) tracked.Add(ModelName, descriptor.Name, enumerable, descriptor.Key, row, index++);
            }
            return true;
        }
        catch (BridgeCollectionKeyException)
        {
            _collectionKeys.Remove(descriptor);
            return false;
        }
    }

    // Whether a frame may replace a row after its own property change: its key must be valid and still the key it was
    // tracked with.
    private bool TrackItemKey(BridgeCollectionDescriptor descriptor, object rows, object item, int index, out string key)
    {
        key = "";
        try { key = BridgeCollectionKeyBaseline.KeyOf(ModelName, descriptor.Name, descriptor.Key, item, index); }
        catch (BridgeCollectionKeyException)
        {
            _collectionKeys.Remove(descriptor);
            return false;
        }
        if (!_collectionKeys.TryGetValue(descriptor, out var tracked) || !ReferenceEquals(tracked.Rows, rows)) return true;
        if (tracked.TrackedKey(item) == key) return true;
        _collectionKeys.Remove(descriptor);
        return false;
    }

    // Logs keys a publication was withheld for and tells the client, once per field, problem and key (not row
    // positions, which unrelated edits shift) until a publication succeeds again.
    private void ReportRejectedKeys(BridgeCollectionKeyException keys)
    {
        if (string.Equals(_rejectedKeys, keys.Identity, StringComparison.Ordinal)) return;
        _rejectedKeys = keys.Identity;
        LogRejectedKeys(keys);
        var failure = RejectedKeysReply(keys);
        _delivery.EnqueueFailure(WritePush(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "failure");
            writer.WriteString("route", _name);
            writer.WriteNumber("revision", _revision);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("kind", failure.Kind);
            writer.WriteString("message", failure.Message);
            BridgeFailureDetails.Write(writer, failure.Detail);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }));
    }

    private BridgeFailure RejectedKeysReply(BridgeCollectionKeyException keys) =>
        new("failed", keys.ReplyMessage, BridgeFailureDetails.Capture(keys, IncludesFailureDetail));

    private void LogRejectedKeys(BridgeCollectionKeyException keys)
    {
        BridgeTelemetry.RecordFailure("collection.keys", ModelName, keys.Field, keys);
        ViewsLog.CollectionKeysRejected(_logger, keys, ModelName, keys.Field, keys.Key ?? "", _name);
    }

    // A value or a reply: an array of its exact length, which a transport may keep.
    private static byte[] WriteValue(Action<Utf8JsonWriter> write)
    {
        using var message = BridgePublicationWriter.Rent();
        write(message.Writer);
        return message.ToArray();
    }

    private static byte[] WriteMessage(Action<Utf8JsonWriter> write) => WriteValue(write);

    // A push in pooled memory, which the delivery returns once the transport took it.
    private static BridgeMessage WritePush(Action<Utf8JsonWriter> write)
    {
        using var message = BridgePublicationWriter.Rent();
        write(message.Writer);
        return message.Detach();
    }

    // One change of a frame.
    private sealed class Change
    {
        private Change(string field) => Field = field;

        public string Field { get; }
        private byte[]? _value;
        private long? _version;
        private BridgeCollectionChange? _indexed;
        private BridgeTextDelta? _text;
        private BridgeTextSplice? _splice;
        private int _length;
        // A splice's text member value after the frame, which a frame without splices sets instead.
        private string? _spliced;

        public bool IsSplice => _splice is not null;

        public static Change Set(string field, byte[] value, long? version) => new(field) { _value = value, _version = version };

        public static Change Indexed(BridgeCollectionChange change) => new(change.Descriptor.Name) { _indexed = change };

        public static Change Text(string field, BridgeTextDelta delta, string _) => new(field) { _text = delta };

        public static Change Splice(string field, int length, BridgeTextSplice splice, long? version, string value) =>
            new(field) { _splice = splice, _length = length, _version = version, _spliced = value };

        public void WriteAsSet(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("field", Field);
            writer.WriteString("kind", "set");
            writer.WriteString("value", _spliced);
            if (_version is { } version) writer.WriteNumber("version", version);
            writer.WriteEndObject();
        }

        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("field", Field);
            if (_indexed is { } change)
            {
                writer.WriteString("kind", change.Kind);
                writer.WriteNumber("index", change.Index);
                writer.WriteNumber("oldIndex", change.OldIndex);
                writer.WriteStartArray("keys");
                foreach (var key in change.Keys) writer.WriteStringValue(key);
                writer.WriteEndArray();
                writer.WriteStartArray("items");
                foreach (var item in change.Items) writer.WriteRawValue(item, skipInputValidation: true);
                writer.WriteEndArray();
            }
            else if (_splice is { } splice)
            {
                writer.WriteString("kind", "splice");
                writer.WriteNumber("length", _length);
                writer.WriteNumber("index", splice.Index);
                writer.WriteNumber("delete", splice.Delete);
                writer.WriteString("text", splice.Text);
                if (_version is { } version) writer.WriteNumber("version", version);
            }
            else if (_text is { } text)
            {
                writer.WriteString("kind", "text");
                writer.WriteNumber("length", text.Length);
                writer.WriteNumber("trim", text.Trim);
                writer.WriteString("text", text.Text);
            }
            else
            {
                writer.WriteString("kind", "set");
                writer.WritePropertyName("value");
                writer.WriteRawValue(_value!, skipInputValidation: true);
                if (_version is { } version) writer.WriteNumber("version", version);
            }
            writer.WriteEndObject();
        }
    }
}
