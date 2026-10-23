using System.Runtime.CompilerServices;

namespace Runic.Application.Views;

// Read-your-writes (I-183): the reply of a command, setter or checked write
// follows the state frames that the call produced on every route of its
// transport. A call often changes another route than its own, as a page
// command that updates its shell does, and that route's state travels
// through its own delivery queue rather than in the reply. The reply waits
// until every delivery of the transport has settled what was handed to it
// before the reply was ready. A transport is one window's connection, so the
// page applies those states before the call's promise resolves.
//
// The wait is bounded: a page that does not take frames for ReplyWaitLimit
// gets the reply anyway. A synchronous route that is called inside the
// model turn it would wait for does not wait at all.
internal sealed class BridgeReplyOrder
{
    internal static readonly TimeSpan ReplyWaitLimit = TimeSpan.FromSeconds(5);
    private static readonly ConditionalWeakTable<IBridgeTransport, BridgeReplyOrder> Orders = new();
    private readonly HashSet<BridgeSnapshotDelivery> _deliveries = [];

    // The deliveries of a transport that have outstanding work.
    internal static BridgeReplyOrder For(IBridgeTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return Orders.GetValue(transport, static _ => new());
    }

    internal void Add(BridgeSnapshotDelivery delivery)
    {
        lock (_deliveries) _deliveries.Add(delivery);
    }

    internal void Remove(BridgeSnapshotDelivery delivery)
    {
        lock (_deliveries) _deliveries.Remove(delivery);
    }

    // Completes when every delivery of the transport has settled the work it
    // had now. Null when none has outstanding work.
    internal Task? WhenPublished()
    {
        BridgeSnapshotDelivery[] deliveries;
        lock (_deliveries)
        {
            if (_deliveries.Count == 0) return null;
            deliveries = [.. _deliveries];
        }
        List<Task>? pending = null;
        foreach (var delivery in deliveries)
            if (delivery.WhenSettled() is { } settled) (pending ??= []).Add(settled);
        return pending switch
        {
            null => null,
            [var single] => single,
            _ => Task.WhenAll(pending),
        };
    }

    // An awaited route: the reply follows its frames, unless the page takes
    // none for ReplyWaitLimit or the call is cancelled.
    internal async ValueTask<string> AfterPublishedAsync(string reply, CancellationToken cancellationToken)
    {
        if (WhenPublished() is not { } published) return reply;
        try { await published.WaitAsync(ReplyWaitLimit, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        return reply;
    }

    // A synchronous route, called outside the model turn: it blocks the host's
    // callback thread, which is not the thread that captures or publishes.
    // Called inside the model turn (`inTurn`), it cannot wait for a capture
    // that needs that same turn, so it replies at once.
    internal string AfterPublished(string reply, bool inTurn)
    {
        if (inTurn || WhenPublished() is not { } published) return reply;
        published.Wait(ReplyWaitLimit);
        return reply;
    }
}
