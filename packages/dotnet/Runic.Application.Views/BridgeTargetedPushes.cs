using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

// The targeted pushes of one window (operation and interaction messages), sent in the order they were queued through
// BridgeDispatcher.SendAsync: to the connection directly, or into its mailbox.
//
// Decision 12 of protocol 4 (specs/application/README.md, "Transport" and "Status"): the state, frame and failure
// pushes of a turn come before the operation and interaction pushes the same turn caused. A push therefore waits until
// every delivery of the window has handed over what it had when the push was queued (BridgeReplyOrder), so a terminal
// status follows the frame with its revision. The wait is bounded like a reply's (BridgeReplyOrder.ReplyWaitLimit).
// Mailbox transports are exempt from the order rule, but keeping it costs nothing.
//
// Sending never runs on the caller's thread: callers queue pushes while they hold their own locks or a model turn.
internal sealed class BridgeTargetedPushes
{
    private static readonly ConditionalWeakTable<IBridgeTransport, BridgeTargetedPushes> Windows = new();

    private readonly Lock _gate = new();
    private readonly Queue<Push> _queue = new();
    private readonly BridgeDispatcher _dispatcher;
    private readonly BridgeReplyOrder _replyOrder;
    private bool _sending;

    private BridgeTargetedPushes(IBridgeTransport transport)
    {
        _dispatcher = BridgeDispatcher.For(transport);
        _replyOrder = BridgeReplyOrder.For(transport);
    }

    internal static BridgeTargetedPushes For(IBridgeTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return Windows.GetValue(transport, static key => new(key));
    }

    // Queues a push to one connection. The message is UTF-8 JSON that the queue owns.
    internal void Send(string connectionKey, byte[] message)
    {
        ArgumentNullException.ThrowIfNull(connectionKey);
        ArgumentNullException.ThrowIfNull(message);
        // What the window's deliveries were handed until now, captured at once: later frames do not hold the push.
        var published = _replyOrder.WhenPublished();
        lock (_gate)
        {
            _queue.Enqueue(new(connectionKey, message, published));
            if (_sending) return;
            _sending = true;
        }
        _ = Task.Run(SendQueuedAsync);
    }

    // Completes when every push queued so far was handed to the transport. Tests wait on it.
    internal Task WhenSent()
    {
        lock (_gate)
        {
            if (!_sending) return Task.CompletedTask;
            _idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task;
        }
    }

    private TaskCompletionSource? _idle;

    private async Task SendQueuedAsync()
    {
        while (true)
        {
            Push push;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out push))
                {
                    _sending = false;
                    _idle?.TrySetResult();
                    _idle = null;
                    return;
                }
            }
            try
            {
                if (push.Published is { } published)
                {
                    try { await published.WaitAsync(BridgeReplyOrder.ReplyWaitLimit, _dispatcher.TimeProvider).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                await _dispatcher.SendAsync(push.ConnectionKey, push.Message).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // A push to a closing transport is dropped (language mappings, "Failed publications").
                ViewsLog.RequestFailed(_dispatcher.Logger, error, BridgeTelemetry.ErrorType(error));
            }
        }
    }

    private readonly record struct Push(string ConnectionKey, byte[] Message, Task? Published);
}
