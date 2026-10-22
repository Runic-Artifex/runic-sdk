using System.Runtime.CompilerServices;

// The ReactiveUI adapters register the executions that an operation's call starts (I-192).
[assembly: InternalsVisibleTo("Runic.Application.Views.ReactiveUI")]
[assembly: InternalsVisibleTo("Runic.Application.Views.ReactiveUI.Reactive")]

namespace Runic.Application.Views;

// The work of one operation declared RunicOperationRetirement.Cancel or Drain that can outlive the operation's call
// (I-192). The registry puts it into the async flow of the operation's work. An integration whose call can return
// before the work it started stopped registers that work: a ReactiveUI command's Bridge call returns `cancelled` once
// cancellation is requested, while the command's body can still run. Retirement releases the page only after the
// operation's terminal and the end of every work registered until then; the operation's status is not delayed.
internal sealed class BridgeOperationWork
{
    private static readonly AsyncLocal<BridgeOperationWork?> Flow = new();
    private readonly Lock _gate = new();
    private List<Task>? _running;
    private Task? _ended;

    // Runs the operation's work in this tracker's flow; null for an operation without one (Keep), so a nested
    // operation does not register its work with the operation that started it.
    internal static void Enter(BridgeOperationWork? work) => Flow.Value = work;

    // Registers work of the operation whose call runs in the current flow; it has ended when the returned source
    // completes, which must never fault. Null when no operation tracks work here or it already reached its terminal.
    internal static TaskCompletionSource? Begin()
    {
        if (Flow.Value is not { } work) return null;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (work._gate)
        {
            if (work._ended is not null) return null;
            (work._running ??= []).Add(ended.Task);
        }
        return ended;
    }

    // At the operation's terminal: nothing registers afterwards. Returns when the registered work ended.
    internal Task Close()
    {
        lock (_gate)
            return _ended ??= _running is null ? Task.CompletedTask : Task.WhenAll(_running);
    }
}
