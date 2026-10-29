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
// Only that direction is ordered. Pushes that later requests caused can reach
// the transport before the reply, because the reply follows its wait through
// continuations the runtime may schedule later; clients rely on revisions
// (specs/application/README.md, "Reply order").
//
// The wait is bounded: a page that does not take frames for ReplyWaitLimit
// gets the reply anyway, and the first reply of a window that waited that long
// is logged (1024). A synchronous route that is called inside the model turn
// it would wait for does not wait at all.
internal sealed class BridgeReplyOrder
{
    internal static readonly TimeSpan ReplyWaitLimit = TimeSpan.FromSeconds(5);
    private static readonly ConditionalWeakTable<IBridgeTransport, BridgeReplyOrder> Orders = new();
    private readonly HashSet<BridgeSnapshotDelivery> _deliveries = [];
    private readonly IBridgeTransport _transport;
    private int _waitExceededLogged;

    private BridgeReplyOrder(IBridgeTransport transport) => _transport = transport;

    // The deliveries of a transport that have outstanding work.
    internal static BridgeReplyOrder For(IBridgeTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return Orders.GetValue(transport, static key => new(key));
    }

    // How long a reply waits for the window's pushes; tests shorten it for their own transport.
    internal TimeSpan WaitLimit { get; set; } = ReplyWaitLimit;

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
        await WaitAsync(published, "", cancellationToken).ConfigureAwait(false);
        return reply;
    }

    // Waits for what WhenPublished returned, at most WaitLimit. The first wait of the window that runs out is logged
    // with the routes that still had pushes to deliver, because the reply then reaches the page before its frames.
    internal async ValueTask WaitAsync(Task published, string route, CancellationToken cancellationToken)
    {
        try { await published.WaitAsync(WaitLimit, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { ReportWaitExceeded(route); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void ReportWaitExceeded(string route)
    {
        if (Interlocked.Exchange(ref _waitExceededLogged, 1) != 0) return;
        string pending;
        lock (_deliveries)
            pending = string.Join(", ", _deliveries.Select(static delivery => delivery.Route).Order(StringComparer.Ordinal));
        ViewsLog.ReplyWaitExceeded(BridgeDispatcher.For(_transport).Logger, route, WaitLimit.TotalSeconds,
            pending.Length == 0 ? "none" : pending);
    }

    // A synchronous route, called outside the model turn: it blocks the host's
    // callback thread, which is not the thread that captures or publishes.
    // Called inside the model turn (`inTurn`), it cannot wait for a capture
    // that needs that same turn, so it replies at once.
    internal string AfterPublished(string reply, bool inTurn)
    {
        if (inTurn || WhenPublished() is not { } published) return reply;
        if (!published.Wait(WaitLimit)) ReportWaitExceeded("");
        return reply;
    }
}
