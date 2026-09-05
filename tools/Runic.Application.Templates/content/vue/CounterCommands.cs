using Runic.Application.Bridge;
using Runic.Application.Bridge.Generated;

namespace RunicDesktopApp;

internal sealed partial class CounterCommands(CounterState state)
{
    [BridgeCommand(AdvancesRevision = true)]
    private async ValueTask<CounterIncremented> IncrementAsync(
        IncrementCounter command, BridgeCommandContext context, CancellationToken cancellationToken)
    {
        CounterSnapshot snapshot = state.Increment(command.Step, context.CurrentRevision + 1);
        await context.Events.PublishCounterChangedAsync(new CounterChanged(snapshot), cancellationToken: cancellationToken);
        return new(snapshot);
    }

    [BridgeCommand(AdvancesRevision = true)]
    private async ValueTask<CounterReset> ResetAsync(
        ResetCounter command, BridgeCommandContext context, CancellationToken cancellationToken)
    {
        CounterSnapshot snapshot = state.Reset(context.CurrentRevision + 1);
        await context.Events.PublishCounterChangedAsync(new CounterChanged(snapshot), cancellationToken: cancellationToken);
        return new(snapshot);
    }
}

internal sealed record IncrementCounter([property: BridgeMinimum(1), BridgeMaximum(10)] int Step);
internal sealed record ResetCounter;
internal sealed record CounterIncremented(CounterSnapshot Snapshot);
internal sealed record CounterReset(CounterSnapshot Snapshot);
[BridgeEvent] internal sealed record CounterChanged(CounterSnapshot Snapshot);
