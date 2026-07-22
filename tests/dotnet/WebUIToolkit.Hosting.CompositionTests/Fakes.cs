using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting.CompositionTests;

internal sealed class RecordingHost(ICollection<string> log) : IApplicationHost
{
    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int DisposeCount { get; private set; }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        log.Add("host:start");
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        log.Add("host:stop");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        log.Add("host:dispose");
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingValidator(
    string name,
    ICollection<string> log,
    params ApplicationValidationError[] errors) : IApplicationValidator
{
    public int CallCount { get; private set; }

    public ValueTask ValidateAsync(
        ApplicationValidationContext context,
        ICollection<ApplicationValidationError> output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        log.Add($"validate:{name}:{context.Decision.Kind}");
        foreach (ApplicationValidationError error in errors)
        {
            output.Add(error);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingParticipant(string name, ICollection<string> log) : IApplicationStartupParticipant
{
    public ApplicationStartPhase Phase { get; set; } = ApplicationStartPhase.Infrastructure;

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        log.Add($"participant:{name}:start");
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        log.Add($"participant:{name}:stop");
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingRunner(
    LaunchKind kind,
    string name,
    ICollection<string> log,
    ApplicationRunResult? result = null) : IApplicationModeRunner
{
    public LaunchKind Kind { get; set; } = kind;

    public int CallCount { get; private set; }

    public LaunchDecision? LastDecision { get; private set; }

    public Func<LaunchDecision, CancellationToken, Task<ApplicationRunResult>>? Handler { get; init; }

    public Task<ApplicationRunResult> RunAsync(
        LaunchDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        CallCount++;
        LastDecision = decision;
        log.Add($"runner:{name}:{decision.Kind}");
        return Handler is null
            ? Task.FromResult(result ?? ApplicationRunResult.FromExitCode(0))
            : Handler(decision, cancellationToken);
    }
}

internal sealed class RecordingResolver(
    LaunchDecision decision,
    ICollection<string> log) : ILaunchIntentResolver
{
    public int CallCount { get; private set; }

    public LaunchDecision Resolve(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        CallCount++;
        log.Add("resolver:resolve");
        return decision;
    }
}

internal sealed class RecordingEventSink(bool throwOnPublish = false) : IApplicationLifecycleEventSink
{
    private readonly object _sync = new();
    private readonly List<ApplicationLifecycleEvent> _events = [];
    private readonly List<CountWaiter> _countWaiters = [];
    private readonly List<IEventWaiter> _eventWaiters = [];

    public IReadOnlyList<ApplicationLifecycleEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }
    }

    public void Publish(ApplicationLifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        List<CountWaiter> completedCountWaiters = [];
        lock (_sync)
        {
            _events.Add(lifecycleEvent);
            for (int index = _countWaiters.Count - 1; index >= 0; index--)
            {
                CountWaiter waiter = _countWaiters[index];
                if (_events.Count >= waiter.Count)
                {
                    _countWaiters.RemoveAt(index);
                    completedCountWaiters.Add(waiter);
                }
            }

            for (int index = _eventWaiters.Count - 1; index >= 0; index--)
            {
                if (_eventWaiters[index].TryComplete(lifecycleEvent))
                {
                    _eventWaiters.RemoveAt(index);
                }
            }
        }

        foreach (CountWaiter waiter in completedCountWaiters)
        {
            waiter.Completion.TrySetResult(true);
        }

        if (throwOnPublish)
        {
            throw new InvalidOperationException("sink-secret");
        }
    }

    public Task WaitForCountAsync(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_sync)
        {
            if (_events.Count >= count)
            {
                return Task.CompletedTask;
            }

            var waiter = new CountWaiter(count);
            _countWaiters.Add(waiter);
            return waiter.Completion.Task;
        }
    }

    public Task<TEvent> WaitForEventAsync<TEvent>()
        where TEvent : ApplicationLifecycleEvent
    {
        lock (_sync)
        {
            foreach (ApplicationLifecycleEvent lifecycleEvent in _events)
            {
                if (lifecycleEvent is TEvent typedEvent)
                {
                    return Task.FromResult(typedEvent);
                }
            }

            var waiter = new EventWaiter<TEvent>();
            _eventWaiters.Add(waiter);
            return waiter.Completion.Task;
        }
    }

    private sealed class CountWaiter(int count)
    {
        public int Count { get; } = count;

        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private interface IEventWaiter
    {
        bool TryComplete(ApplicationLifecycleEvent lifecycleEvent);
    }

    private sealed class EventWaiter<TEvent> : IEventWaiter
        where TEvent : ApplicationLifecycleEvent
    {
        public TaskCompletionSource<TEvent> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryComplete(ApplicationLifecycleEvent lifecycleEvent)
        {
            if (lifecycleEvent is not TEvent typedEvent)
            {
                return false;
            }

            Completion.TrySetResult(typedEvent);
            return true;
        }
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private DateTimeOffset _utcNow = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_sync)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new InertTimer();
    }

    public void Advance(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);

        lock (_sync)
        {
            _timestamp = checked(_timestamp + amount.Ticks);
            _utcNow = _utcNow.Add(amount);
        }
    }

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class StopAdvancingEventSink(
    ManualTimeProvider timeProvider,
    TimeSpan amount) : IApplicationLifecycleEventSink
{
    private readonly object _sync = new();
    private readonly List<ApplicationLifecycleEvent> _events = [];
    private readonly TaskCompletionSource<bool> _completionObserved =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<ApplicationLifecycleEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }
    }

    public Task CompletionObserved => _completionObserved.Task;

    public void Publish(ApplicationLifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        lock (_sync)
        {
            _events.Add(lifecycleEvent);
        }

        if (lifecycleEvent is ApplicationStopRequestedEvent)
        {
            timeProvider.Advance(amount);
        }
        else if (lifecycleEvent is ApplicationCompletionEvent)
        {
            _completionObserved.TrySetResult(true);
        }
    }
}

internal sealed class FlakyResolver(
    LaunchDecision success,
    Exception? firstException = null,
    bool returnNullFirst = false) : ILaunchIntentResolver
{
    public int CallCount { get; private set; }

    public LaunchDecision Resolve(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        CallCount++;
        if (CallCount == 1)
        {
            if (firstException is not null)
            {
                throw firstException;
            }

            if (returnNullFirst)
            {
                return null!;
            }
        }

        return success;
    }
}

internal sealed class BlockingResolver(LaunchDecision decision) : ILaunchIntentResolver, IDisposable
{
    private readonly ManualResetEventSlim _release = new(initialState: false);

    public TaskCompletionSource Entered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CallCount { get; private set; }

    public LaunchDecision Resolve(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        CallCount++;
        Entered.TrySetResult();
        _release.Wait();
        return decision;
    }

    public void Release() => _release.Set();

    public void Dispose() => _release.Dispose();
}

internal sealed class ThrowingExitCodePolicy : IExitCodePolicy
{
    public int GetExitCode(ApplicationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        throw new InvalidOperationException("policy-secret");
    }
}

internal sealed class ReentrantDisposeSink : IApplicationLifecycleEventSink
{
    private readonly List<ApplicationLifecycleEvent> _events = [];
    private readonly TaskCompletionSource<bool> _completionObserved =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WebUIToolkitApplication? Application { get; set; }

    public IReadOnlyList<ApplicationLifecycleEvent> Events => _events;

    public Task CompletionObserved => _completionObserved.Task;

    public void Publish(ApplicationLifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        _events.Add(lifecycleEvent);
        if (lifecycleEvent is ApplicationCompletionEvent)
        {
            try
            {
                (Application ?? throw new InvalidOperationException("Application was not assigned."))
                    .DisposeAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                _completionObserved.TrySetResult(true);
            }
        }
    }
}
