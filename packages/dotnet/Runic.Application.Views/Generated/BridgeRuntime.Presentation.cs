namespace Runic.Application.Views.Generated;

// The presentation features of a Bridge's route (specs/application/README.md, "Presentation epochs" and "Presented
// states"):
//
// - presentation-epoch/1: the route's epoch is the revision of the state that began it, the route's first state or a
//   renewal (RunicPresentation.Renew). A renewal is pending until the next unit publishes it as a full state at a new
//   revision; a command that carries an older epoch, or any epoch while a renewal is pending, is superseded in its model
//   turn. A route whose open presentations resume on this Bridge after a suspension pushes its first state.
// - presented/1: RunicPresentation.WaitForPresentedAsync waits until a page reports that it painted the route at the
//   revision that includes everything published before the wait. While a wait is pending, each connection that uses
//   the feature and has a presentation of the route open is asked once with a present push, also one that opens the
//   route during the wait.
public partial class ViewModelBridge<T> : IBridgePresentationRoute, IBridgeResumedPresentations
{
    // The route's current epoch: the revision of the state that began it. Read and written in model turns.
    private long _epoch;
    // 1 while a renewal is requested and not yet published; set from any thread.
    private int _renewal;
    // The presented waits of the route and the connections each was sent to, guarded by _presentedGate.
    private readonly object _presentedGate = new();
    private readonly List<PresentedWait> _presentedWaits = [];

    private bool RenewalPending => Volatile.Read(ref _renewal) != 0;

    // RunicPresentation.Renew: the next unit publishes a new epoch. The check of scoped commands sees it at once.
    void IBridgePresentationRoute.Renew()
    {
        if (IsInactive) return;
        Volatile.Write(ref _renewal, 1);
        // The unit runs in a later model turn, or with the reply of the request whose turn renewed the route.
        _delivery.RequestUnit();
    }

    TimeProvider IBridgePresentationRoute.TimeProvider => _content?.TimeProvider ?? BridgeDispatcher.For(_transport).TimeProvider;

    // A renewal: the route's full state at a new revision, which begins the new epoch; the model gate is held, in a
    // model turn. A state with invalid keys is withheld, and the renewal stays pending.
    private void PublishRenewal(long changed)
    {
        Volatile.Write(ref _renewal, 0);
        // The full state covers a state that a batch deferred.
        _stateDeferred = false;
        var epoch = _epoch;
        _revision = NextRevision();
        _epoch = _revision;
        BridgeMessage? state;
        try { state = WriteStatePush(); }
        catch (BridgeCollectionKeyException keys)
        {
            _epoch = epoch;
            Volatile.Write(ref _renewal, 1);
            Withhold(keys);
            Published(changed);
            return;
        }
        if (state is null) return;
        if (IsInactive) state.Release();
        else _delivery.EnqueueState(state);
        ReleaseFirstState();
    }

    // The reply of a superseded command, in its model turn: nothing ran, and its revision is the route's after what was
    // pending was published, as a renewal that superseded it.
    private byte[] SupersededReply()
    {
        var revision = PublishForReply();
        static void WriteSuperseded(System.Text.Json.Utf8JsonWriter writer) => writer.WriteBoolean("superseded", true);
        return IsInactive
            ? EncodeRanReply(null, revision: null, released: true, WriteSuperseded)
            : EncodeRanReply(null, revision ?? _publishedRevision, released: false, WriteSuperseded);
    }

    // The open presentations of the route continue on this Bridge after a suspension: they get its first state.
    void IBridgeResumedPresentations.PresentationsResumed()
    {
        if (IsInactive) return;
        _delivery.RequestState();
        // A wait that this route took over before the resumption asks the resumed presentations too.
        SendPresent(connection: null);
    }

    // A page painted the route at revision or later.
    private void Presented(long revision)
    {
        lock (_presentedGate)
            _presentedWaits.RemoveAll(wait => wait.Wait.Task.IsCompleted || (revision >= wait.Revision && wait.Wait.Complete(true)));
    }

    // WaitForPresentedAsync of a ViewModel that this route presents: publishes what is pending, then waits for the
    // route's revision. Outside a model turn of the model, or inside one.
    void IBridgePresentationRoute.AddPresentedWait(BridgePresentedWait wait)
    {
        long? revision;
        try
        {
            revision = _modelTurn.Run(() =>
            {
                lock (_modelGate)
                {
                    if (IsInactive) return (long?)null;
                    PublishUnitCore();
                    return IsInactive ? null : _revision;
                }
            });
        }
        catch (ObjectDisposedException) { return; }
        if (revision is not { } published) return;
        lock (_presentedGate) _presentedWaits.Add(new(wait, published));
        SendPresent(connection: null);
    }

    // WaitForPresentedAsync of a ViewModel that no route presented yet, which this route is the first to present: the
    // wait counts with the route's first state. No page has opened the route yet.
    void IBridgePresentationRoute.AddFirstStateWait(BridgePresentedWait wait)
    {
        lock (_presentedGate) _presentedWaits.Add(new(wait, _revision));
    }

    // Sends present for each pending wait to the connections that use presented/1 and have the route open, or to one
    // such connection, each at most once per wait.
    private void SendPresent(BridgeConnection? connection)
    {
        string[] targets;
        if (connection is not null)
        {
            if (!connection.UsesPresented) return;
            targets = [connection.Key];
        }
        else
        {
            lock (_presentedGate)
                if (_presentedWaits.Count == 0) return;
            targets = _content?.PresentationConnections(_name) ?? PresentationConnections();
        }
        var dispatcher = BridgeDispatcher.For(_transport);
        List<(string Connection, long Revision)> pushes = [];
        lock (_presentedGate)
        {
            _presentedWaits.RemoveAll(wait => wait.Wait.Task.IsCompleted);
            foreach (var target in targets)
            {
                if (connection is null && dispatcher.FindConnection(target) is not { UsesPresented: true }) continue;
                foreach (var wait in _presentedWaits)
                    if (wait.SentTo.Add(target)) pushes.Add((target, wait.Revision));
            }
        }
        var targeted = BridgeTargetedPushes.For(_transport);
        foreach (var (target, revision) in pushes)
            targeted.Send(target, BridgeMessages.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "present");
                writer.WriteString("route", _name);
                writer.WriteNumber("revision", revision);
                writer.WriteEndObject();
            }));
    }

    // The connections with a presentation of the route of a Bridge without a window.
    private string[] PresentationConnections()
    {
        lock (_modelGate)
            return _presentations is null ? []
                : [.. _presentations.Owners.Values.Select(owner => owner.ConnectionKey).Distinct(StringComparer.Ordinal)];
    }

    private sealed class PresentedWait(BridgePresentedWait wait, long revision)
    {
        public BridgePresentedWait Wait { get; } = wait;
        public long Revision { get; } = revision;
        public HashSet<string> SentTo { get; } = new(StringComparer.Ordinal);
    }
}
