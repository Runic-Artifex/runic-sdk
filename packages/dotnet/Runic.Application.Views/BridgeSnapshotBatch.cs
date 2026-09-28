using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Runic.Application.Views;

/// <summary>
/// Defers notification-driven bridge snapshots for one model until the outermost
/// scope ends. Use this around a synchronous bulk mutation:
/// <code>using var batch = BridgeSnapshotBatch.Begin(viewModel);</code>
/// </summary>
/// <remarks>
/// The scope does not hold the model turn or a model lock while user code runs.
/// It is deliberately synchronous; do not carry it across an <c>await</c>.
/// Direct route replies, including checked-write receipts and command replies,
/// still capture their state immediately.
/// </remarks>
public static class BridgeSnapshotBatch
{
    private static readonly ConditionalWeakTable<object, State> States = new();

    /// <summary>Begins a model-local snapshot batch.</summary>
    public static IDisposable Begin(object model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return States.GetValue(model, static _ => new State()).Enter();
    }

    internal static bool TryDefer(object model, IBridgeSnapshotBatchParticipant participant)
    {
        return States.GetValue(model, static _ => new State()).TryDefer(participant);
    }

    private sealed class State
    {
        private readonly object _gate = new();
        private HashSet<IBridgeSnapshotBatchParticipant>? _pending;
        private int _depth;

        internal IDisposable Enter()
        {
            lock (_gate) _depth++;
            return new Scope(this);
        }

        internal bool TryDefer(IBridgeSnapshotBatchParticipant participant)
        {
            lock (_gate)
            {
                if (_depth == 0) return false;
                (_pending ??= []).Add(participant);
                return true;
            }
        }

        private void Exit()
        {
            IBridgeSnapshotBatchParticipant[]? pending = null;
            lock (_gate)
            {
                if (_depth == 0) return;
                if (--_depth != 0 || _pending is not { Count: > 0 }) return;
                pending = [.. _pending];
                _pending.Clear();
            }

            // Flushing outside the batch gate is essential: a snapshot writer
            // may synchronously re-enter application code or a model context.
            // A broken writer must not prevent the other presentations of this
            // model from receiving their final state.
            Exception? failure = null;
            foreach (var bridge in pending)
            {
                try { bridge.FlushSnapshotBatch(); }
                catch (Exception error) { failure ??= error; }
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private sealed class Scope(State owner) : IDisposable
        {
            private State? _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.Exit();
            }
        }
    }
}

internal interface IBridgeSnapshotBatchParticipant
{
    void FlushSnapshotBatch();
}
