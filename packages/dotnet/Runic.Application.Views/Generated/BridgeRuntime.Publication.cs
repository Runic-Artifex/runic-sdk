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
// is written (BridgeComparingWriter) and keeps the bytes only of members that differ. Pushes are written into pooled
// buffers that the delivery returns once the transport took them; a full state writes its members straight into the
// message. The route's first state, captured at attach, stays as an encoded state until the next change, so an open
// before then copies it instead of encoding every member again.
public partial class ViewModelBridge<T>
{
    // The encoding of each compared member as last published, or null before the route's first state
    // (CaptureFirstState).
    private Dictionary<string, byte[]>? _baselines;
    // The version of each checked field as last published.
    private readonly Dictionary<string, long> _publishedVersions = new(StringComparer.Ordinal);
    // Keyed collections whose next publication is a set of the field: reset, replaced, changed in a way indexed changes
    // cannot express, or withheld because of invalid keys.
    private readonly HashSet<BridgeCollectionDescriptor<T>> _collectionSets = [];
    // The instance of each keyed collection the pending changes describe.
    private readonly Dictionary<BridgeCollectionDescriptor<T>, object?> _collectionInstances = [];
    // The keys of each frame-publishing collection as of the last full state and the frames since, so a change that
    // adds a duplicate key is found at once.
    private readonly Dictionary<BridgeCollectionDescriptor<T>, BridgeCollectionKeyBaseline> _collectionKeys = [];
    private readonly List<BridgeCollectionChange<T>> _pendingCollectionChanges = [];
    private readonly Dictionary<BridgeCollectionDescriptor<T>, Dictionary<object, int>> _collectionIndexes = [];
    // Append-only text fields and text members: the value of each as last published.
    private readonly Dictionary<PropertyDescriptor<T>, string?> _textBaselines = [];
    // Text members: the revision of the last publication that changed each, so a set's splice can name the published
    // value by a revision at or after it (specs/application/state-changes.md, "Text splices").
    private readonly Dictionary<PropertyDescriptor<T>, long> _textChangedRevisions = [];
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
        _dataSubscriptions?.SweepPending();
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
            _pendingCollectionChanges.Clear();
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
        var indexed = new Dictionary<BridgeCollectionDescriptor<T>, List<BridgeCollectionChange<T>>>();
        foreach (var change in _pendingCollectionChanges)
        {
            if (_collectionSets.Contains(change.Descriptor)) continue;
            if (!indexed.TryGetValue(change.Descriptor, out var list)) indexed.Add(change.Descriptor, list = []);
            list.Add(change);
        }
        var sets = new List<BridgeCollectionDescriptor<T>>(_collectionSets);
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
                    _collectionKeys[descriptor] = BridgeCollectionKeyBaseline.Scan(ModelName, descriptor.Name, enumerable, descriptor.Key);
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
                BridgeCollectionKeyBaseline.Scan(ModelName, keyed.Name, rows, keyed.Key);
        changes.AddRange(memberSets);
        changes.AddRange(texts);
        return changes;
    }

    private long? VersionOf(string member) =>
        _checkedByMember.TryGetValue(member, out var field) ? field.Registry.Current.Version : null;

    // The baselines after a published frame: members holds the members that changed.
    private void Commit(List<(string Name, byte[] Value)> members, long revision)
    {
        foreach (var (name, value) in members) _baselines![name] = value;
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
        _pendingCollectionChanges.Clear();
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
        _pendingCollectionChanges.Clear();
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
            _pendingCollectionChanges.Clear();
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
            _pendingCollectionChanges.Clear();
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
        var changed = _changes;
        var writer = message.Writer;
        List<(string Name, long Start, long End)>? compared = updateBaselines ? new(_stateMembers.Length) : null;
        long stateStart, stateEnd;
        using (BridgeSnapshotPublication.Enter(() => IsInactive))
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
                    member.Write(writer, _vm);
                    continue;
                }
                var start = message.Position;
                member.Write(writer, _vm);
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
        foreach (var descriptor in _incrementalCollections) _collectionInstances[descriptor] = descriptor.Get(_vm);
        Commit([], _revision);
        // The state is a unit that compared every member.
        Published(changed);
        return state;
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
    // allocates nothing.
    private List<(string Name, byte[] Value)> WriteChangedMembers()
    {
        var changed = new List<(string Name, byte[] Value)>();
        using (BridgeSnapshotPublication.Enter(() => IsInactive))
        {
            foreach (var member in _stateMembers)
            {
                if (_incrementalMembers.Contains(member.Name)) continue;
                _baselines!.TryGetValue(member.Name, out var previous);
                var comparing = BridgeComparingWriter.Rent(previous);
                try
                {
                    member.Write(comparing.Writer, _vm);
                    var matches = comparing.Matches;
                    var version = VersionOf(member.Name);
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

    // The encoding of one member.
    private byte[] WriteMember(string name)
    {
        using (BridgeSnapshotPublication.Enter(() => IsInactive))
            foreach (var member in _stateMembers)
                if (member.Name == name) return WriteValue(writer => member.Write(writer, _vm));
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
    private string? PublishedText(PropertyDescriptor<T> member, long revision) =>
        revision <= _publishedRevision && revision >= _textChangedRevisions.GetValueOrDefault(member)
            && _textBaselines.TryGetValue(member, out var value) ? value : null;

    // A keyed collection's next publication is a set of the field.
    private void MarkCollectionSet(BridgeCollectionDescriptor<T> descriptor)
    {
        if (_baselines is null) return;
        _collectionSets.Add(descriptor);
        _pendingCollectionChanges.RemoveAll(change => change.Descriptor == descriptor);
        _collectionKeys.Remove(descriptor);
    }

    // A closed model context makes the change one for a released Bridge: handled, nothing published.
    private bool TryPublishCollection(INotifyCollectionChanged collection, NotifyCollectionChangedEventArgs args)
    {
        return _modelTurn.RunNotification(() =>
        {
            lock (_modelGate)
            {
                if (IsInactive) return true;
                var descriptors = _incrementalCollections.Where(descriptor => ReferenceEquals(descriptor.Get(_vm), collection)).ToArray();
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
                    if (_collectionSets.Contains(descriptor)) continue;
                    // A change without a position, or with invalid keys, becomes a set of the field; the unit checks
                    // the keys and withholds the publication while they are invalid.
                    if (!indexed || !TrackCollectionKeys(descriptor, collection, args))
                    {
                        MarkCollectionSet(descriptor);
                        continue;
                    }
                    var keys = (kind == "add" ? args.NewItems : args.OldItems)?.Cast<object?>().Select(descriptor.Key).ToArray() ?? [];
                    var items = kind is "add" or "replace"
                        ? args.NewItems?.Cast<object?>().Select(item => WriteValue(writer => descriptor.WriteItem(writer, item))).ToArray() ?? []
                        : [];
                    _pendingCollectionChanges.Add(new(descriptor, kind,
                        kind == "remove" ? args.OldStartingIndex : args.NewStartingIndex,
                        args.OldStartingIndex, keys, items));
                    if (_pendingCollectionChanges.Count(change => change.Descriptor == descriptor) > MaximumChanges)
                        MarkCollectionSet(descriptor);
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
                    if (!_collectionIndexes.TryGetValue(descriptor, out var indexes))
                    {
                        indexes = new(ReferenceEqualityComparer.Instance);
                        for (var index = 0; index < rows.Count; index++)
                            if (rows[index] is { } row) indexes[row] = index;
                        _collectionIndexes[descriptor] = indexes;
                    }
                    if (!indexes.TryGetValue(item, out var at) || at >= rows.Count || !ReferenceEquals(rows[at], item)) continue;
                    handled = true;
                    if (_baselines is null || _collectionSets.Contains(descriptor)) continue;
                    // A key changed in place or made invalid becomes a set of the field.
                    if (!TrackItemKey(descriptor, rows, item, at, out var key)) MarkCollectionSet(descriptor);
                    else _pendingCollectionChanges.Add(new(descriptor, "replace", at, at,
                        [key], [WriteValue(writer => descriptor.WriteItem(writer, item))]));
                }
                if (handled) ScheduleUnit();
                return handled;
            }
        }, closed: true);
    }

    // Checks every keyed collection's keys and makes them the baseline for frames: a state whose keyed collection has a
    // null row or a null, empty or duplicate key is never written (BridgeCollectionKeyException).
    private void CheckCollectionKeys()
    {
        foreach (var descriptor in _keyedCollections)
        {
            _collectionKeys.Remove(descriptor);
            if (descriptor.Get(_vm) is not System.Collections.IEnumerable rows) continue;
            var baseline = BridgeCollectionKeyBaseline.Scan(ModelName, descriptor.Name, rows, descriptor.Key);
            if (descriptor.PublishesChanges) _collectionKeys[descriptor] = baseline;
        }
    }

    // Updates the key baseline for one indexed collection notification and returns whether a frame may describe it. It
    // never throws into the code that changed the collection.
    private bool TrackCollectionKeys(BridgeCollectionDescriptor<T> descriptor, object rows, NotifyCollectionChangedEventArgs args)
    {
        var enumerable = (System.Collections.IEnumerable)rows;
        try
        {
            if (!_collectionKeys.TryGetValue(descriptor, out var tracked) || !ReferenceEquals(tracked.Rows, rows))
            {
                _collectionKeys[descriptor] = BridgeCollectionKeyBaseline.Scan(ModelName, descriptor.Name, enumerable, descriptor.Key);
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
    private bool TrackItemKey(BridgeCollectionDescriptor<T> descriptor, object rows, object item, int index, out string key)
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
        private BridgeCollectionChange<T>? _indexed;
        private BridgeTextDelta? _text;
        private BridgeTextSplice? _splice;
        private int _length;
        // A splice's text member value after the frame, which a frame without splices sets instead.
        private string? _spliced;

        public bool IsSplice => _splice is not null;

        public static Change Set(string field, byte[] value, long? version) => new(field) { _value = value, _version = version };

        public static Change Indexed(BridgeCollectionChange<T> change) => new(change.Descriptor.Name) { _indexed = change };

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
