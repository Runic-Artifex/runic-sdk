using Runic.Application.Bridge;
using Runic.Application.Template.Contract;

namespace RunicDesktopApp;

internal sealed class CounterBridgeHandler : ICounterBridgeHandler
{
    private readonly Lock _gate = new();
    private readonly List<long> _history = [0];
    private long _count;

    public ValueTask<ApplicationInitialized> InitializeApplicationAsync(
        InitializeApplication command,
        BridgeCommandContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(new ApplicationInitialized
            {
                Tag = "ApplicationInitialized",
                Snapshot = InitialSnapshot(context.CurrentRevision),
            });
        }
    }

    public async ValueTask<CounterIncremented> IncrementCounterAsync(
        IncrementCounter command,
        BridgeCommandContext context,
        CancellationToken cancellationToken)
    {
        CounterIncrementedSnapshot receipt;
        CounterChanged changed;
        lock (_gate)
        {
            _count += command.Step;
            _history.Add(_count);
            long revision = context.CurrentRevision + 1;
            receipt = IncrementedSnapshot(revision);
            changed = ChangedEvent(revision);
        }
        await context.Events.PublishCounterChangedAsync(changed, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new CounterIncremented { Tag = "CounterIncremented", Snapshot = receipt };
    }

    public async ValueTask<CounterReset> ResetCounterAsync(
        ResetCounter command,
        BridgeCommandContext context,
        CancellationToken cancellationToken)
    {
        CounterResetSnapshot receipt;
        CounterChanged changed;
        lock (_gate)
        {
            _count = 0;
            _history.Clear();
            _history.Add(0);
            long revision = context.CurrentRevision + 1;
            receipt = ResetSnapshot(revision);
            changed = ChangedEvent(revision);
        }
        await context.Events.PublishCounterChangedAsync(changed, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new CounterReset { Tag = "CounterReset", Snapshot = receipt };
    }

    private ApplicationInitializedSnapshot InitialSnapshot(long revision) => new()
    {
        Count = _count,
        History = _history.ToArray(),
        Revision = revision,
    };

    private CounterIncrementedSnapshot IncrementedSnapshot(long revision) => new()
    {
        Count = _count,
        History = _history.ToArray(),
        Revision = revision,
    };

    private CounterResetSnapshot ResetSnapshot(long revision) => new()
    {
        Count = _count,
        History = _history.ToArray(),
        Revision = revision,
    };

    private CounterChanged ChangedEvent(long revision) => new()
    {
        Tag = "CounterChanged",
        Snapshot = new CounterChangedSnapshot
        {
            Count = _count,
            History = _history.ToArray(),
            Revision = revision,
        },
    };
}
