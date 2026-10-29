using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Runic.Application.Views.Generated;

// The publication of a Bridge's state (specs/application/state-changes.md, "Producing frames").
//
// A publication unit compares the encoding of each member other than keyed collections and append-only text fields with
// the encoding last published on the route, and publishes every member that differs as a set change, together with the
// pending changes of keyed collections and the text appended to text fields, as one frame at the window's next
// revision. A unit that changes nothing publishes nothing.
//
// Notifications request a unit, which the delivery runs in a later model turn, so a burst of notifications publishes
// one frame. A BridgeSnapshotBatch publishes its unit when the outermost batch ends. A request that changes state
// publishes its unit before it replies (PublishForReply), so a change without a notification is a frame before the
// reply. A full state is published only after the delivery queue overflowed; open and snapshot replies carry one.
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
    // Append-only text: the value of each field as last published.
    private readonly Dictionary<PropertyDescriptor<T>, string?> _textBaselines = [];
    // The keys a publication was last withheld for, reported once until a publication succeeds.
    private string? _rejectedKeys;
    // Whether the route's publications are withheld because a keyed collection has invalid keys.
    private bool _withheld;
    // Whether a requested state capture was deferred by a batch.
    private bool _stateDeferred;
    // The revision of the last state or frame published on the route.
    private long _publishedRevision;
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
    private void ScheduleUnit() => _modelTurn.Run(() =>
    {
        lock (_modelGate)
        {
            if (IsInactive) return;
            if (BridgeSnapshotBatch.TryDefer(_vm, this)) return;
            _delivery.RequestUnit();
        }
    });

    void IBridgeSnapshotBatchParticipant.FlushSnapshotBatch() => _modelTurn.Run(() =>
    {
        lock (_modelGate) PublishUnitCore();
    });

    // Called by the delivery in a model turn.
    private void RunRequestedUnit()
    {
        lock (_modelGate)
        {
            // A batch still open publishes its unit when it ends.
            if (IsInactive || BridgeSnapshotBatch.TryDefer(_vm, this)) return;
            PublishUnitCore();
        }
    }

    private long NextRevision() => _content?.NextRevision() ?? _revision + 1;

    // Publishes one unit; the model gate is held, in a model turn.
    private void PublishUnitCore()
    {
        if (IsInactive) return;
        if (_baselines is null)
        {
            CaptureFirstState();
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
            return;
        }
        List<Change> changes;
        Dictionary<string, byte[]> members;
        try
        {
            members = WriteMembers(includeIncremental: false);
            changes = Compare(members);
        }
        catch (BridgeSnapshotDetachedException) { return; }
        catch (BridgeCollectionKeyException keys)
        {
            Withhold(keys);
            return;
        }
        if (changes.Count == 0) return;
        var revision = NextRevision();
        _revision = revision;
        var frame = WriteFrame(changes, revision);
        if (!_delivery.EnqueueFrame(frame))
        {
            PublishRecoveryState();
            return;
        }
        Commit(members, revision);
    }

    // The changes of a unit against the published baselines; throws BridgeCollectionKeyException when a keyed
    // collection that becomes a set has invalid keys.
    private List<Change> Compare(Dictionary<string, byte[]> members)
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
        var memberSets = new List<Change>();
        foreach (var (name, value) in members)
        {
            var version = VersionOf(name);
            if (_baselines!.TryGetValue(name, out var previous) && previous.AsSpan().SequenceEqual(value)
                && (version is null || _publishedVersions.GetValueOrDefault(name) == version)) continue;
            memberSets.Add(Change.Set(name, value, version));
        }
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

    // The baselines after a published frame.
    private void Commit(Dictionary<string, byte[]> members, long revision)
    {
        foreach (var (name, value) in members) _baselines![name] = value;
        foreach (var field in _checkedProperties) _publishedVersions[field.Descriptor.WireName] = field.Registry.Current.Version;
        foreach (var field in _textFields) _textBaselines[field] = field.Get(_vm) as string;
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
        byte[]? state;
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
        if (state is not null && !IsInactive) _delivery.EnqueueState(state);
    }

    // The route's first state, captured in the first unit after the Bridge attached, whether or not a page is
    // connected. Writing it presents the content of the View's content slots, which binds that content to the window's
    // model context, so what the window presents follows the model rather than when a page first reads it. Nothing is
    // pushed: it is the baseline of the route's frames, and an open or snapshot reads the state at its revision. While a
    // keyed collection has invalid keys there is no first state; the next unit tries again, and a read reports them.
    private void CaptureFirstState()
    {
        try
        {
            WriteMessage(writer =>
            {
                writer.WriteStartObject();
                WriteState(writer, updateBaselines: true);
                writer.WriteEndObject();
            });
        }
        catch (BridgeSnapshotDetachedException) { }
        catch (BridgeCollectionKeyException) { }
    }

    // Called by the delivery in a model turn after it took the request.
    private byte[]? CaptureRequestedState()
    {
        lock (_modelGate)
        {
            if (IsInactive) return null;
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

    private byte[]? WriteStatePush()
    {
        try
        {
            return WriteMessage(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "state");
                writer.WriteString("route", _name);
                WriteState(writer, updateBaselines: true);
                writer.WriteEndObject();
            });
        }
        // The session detached this route meanwhile; there is nothing to publish.
        catch (BridgeSnapshotDetachedException) { return null; }
    }

    // Writes "revision", "state" and "fields" of the route's full state into an open object, at the route's latest
    // revision. With updateBaselines, the state becomes the baseline of the next frame (a published state, or a reply's
    // state, which equals the published one). Throws BridgeCollectionKeyException for invalid keys.
    private void WriteState(Utf8JsonWriter writer, bool updateBaselines)
    {
        var members = WriteMembers(includeIncremental: true, checkKeys: true, out var ordered);
        writer.WriteNumber("revision", _revision);
        writer.WriteStartObject("state");
        foreach (var name in ordered)
        {
            writer.WritePropertyName(name);
            writer.WriteRawValue(members[name], skipInputValidation: true);
        }
        writer.WriteEndObject();
        if (_checkedProperties.Length > 0)
        {
            writer.WriteStartObject("fields");
            foreach (var field in _checkedProperties)
            {
                writer.WriteStartObject(field.Descriptor.WireName);
                writer.WriteNumber("version", field.Registry.Current.Version);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        if (!updateBaselines) return;
        _baselines ??= new(StringComparer.Ordinal);
        foreach (var (name, value) in members)
            if (!_incrementalMembers.Contains(name)) _baselines[name] = value;
        foreach (var descriptor in _incrementalCollections) _collectionInstances[descriptor] = descriptor.Get(_vm);
        Commit([], _revision);
    }

    // Writes the members; a protocol-3 writer writes them all. With checkKeys, every keyed collection's keys are
    // checked and become the baseline for frames.
    private Dictionary<string, byte[]> WriteMembers(bool includeIncremental) =>
        WriteMembers(includeIncremental, checkKeys: false, out _);

    private Dictionary<string, byte[]> WriteMembers(bool includeIncremental, bool checkKeys, out List<string> ordered)
    {
        var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        ordered = [];
        using (BridgeSnapshotPublication.Enter(() => IsInactive))
        {
            if (checkKeys) CheckCollectionKeys();
            foreach (var member in _stateMembers)
            {
                if (!includeIncremental && _incrementalMembers.Contains(member.Name)) continue;
                members[member.Name] = WriteValue(writer => member.Write(writer, _vm));
                ordered.Add(member.Name);
            }
        }
        BridgeSnapshotPublication.ThrowIfInactive();
        if (IsInactive) throw new BridgeSnapshotDetachedException();
        return members;
    }

    // The encoding of one member.
    private byte[] WriteMember(string name)
    {
        using (BridgeSnapshotPublication.Enter(() => IsInactive))
            foreach (var member in _stateMembers)
                if (member.Name == name) return WriteValue(writer => member.Write(writer, _vm));
        throw new InvalidOperationException($"{ModelName} has no state member '{name}'.");
    }

    private byte[] WriteFrame(List<Change> changes, long revision) => WriteMessage(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "frame");
        writer.WriteString("route", _name);
        writer.WriteNumber("baseRevision", _publishedRevision);
        writer.WriteNumber("revision", revision);
        writer.WriteStartArray("changes");
        foreach (var change in changes) change.Write(writer);
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    // A keyed collection's next publication is a set of the field.
    private void MarkCollectionSet(BridgeCollectionDescriptor<T> descriptor)
    {
        if (_baselines is null) return;
        _collectionSets.Add(descriptor);
        _pendingCollectionChanges.RemoveAll(change => change.Descriptor == descriptor);
        _collectionKeys.Remove(descriptor);
    }

    private bool TryPublishCollection(INotifyCollectionChanged collection, NotifyCollectionChangedEventArgs args)
    {
        return _modelTurn.Run(() =>
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
                        ? args.NewItems?.Cast<object?>().Select(item => WriteText(writer => descriptor.WriteItem(writer, item))).ToArray() ?? []
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
        });
    }

    private bool TryPublishItem(object item)
    {
        return _modelTurn.Run(() =>
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
                        [key], [WriteText(writer => descriptor.WriteItem(writer, item))]));
                }
                if (handled) ScheduleUnit();
                return handled;
            }
        });
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
        _delivery.EnqueueFailure(WriteMessage(writer =>
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

    private static byte[] WriteValue(Action<Utf8JsonWriter> write)
    {
        using var scratch = BridgeJsonScratch.Rent();
        write(scratch.Writer);
        scratch.Writer.Flush();
        return scratch.Written.ToArray();
    }

    private static byte[] WriteMessage(Action<Utf8JsonWriter> write) => WriteValue(write);

    private static string WriteText(Action<Utf8JsonWriter> write) => System.Text.Encoding.UTF8.GetString(WriteValue(write));

    // One change of a frame.
    private sealed class Change
    {
        private Change(string field) => Field = field;

        public string Field { get; }
        private byte[]? _value;
        private long? _version;
        private BridgeCollectionChange<T>? _indexed;
        private BridgeTextDelta? _text;

        public static Change Set(string field, byte[] value, long? version) => new(field) { _value = value, _version = version };

        public static Change Indexed(BridgeCollectionChange<T> change) => new(change.Descriptor.Name) { _indexed = change };

        public static Change Text(string field, BridgeTextDelta delta, string _) => new(field) { _text = delta };

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
