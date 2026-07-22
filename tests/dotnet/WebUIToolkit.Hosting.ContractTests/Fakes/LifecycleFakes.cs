using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.Hosting;

namespace WebUIToolkit.Hosting.ContractTests.Fakes;

internal sealed class EventLog
{
    private readonly object _sync = new();
    private readonly List<string> _events = [];

    public IReadOnlyList<string> Snapshot()
    {
        lock (_sync)
        {
            return _events.ToArray();
        }
    }

    public void Add(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_sync)
        {
            _events.Add(value);
        }
    }
}

internal sealed class FakeApplicationHost(EventLog events) : IApplicationHost
{
    public Func<CancellationToken, ValueTask>? StartOperation { get; init; }

    public Func<CancellationToken, ValueTask>? StopOperation { get; init; }

    public Func<ValueTask>? DisposeOperation { get; init; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int DisposeCount { get; private set; }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        events.Add("host.start");
        return StartOperation?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        StopCount++;
        events.Add("host.stop");
        return StopOperation?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        events.Add("host.dispose");
        return DisposeOperation?.Invoke() ?? ValueTask.CompletedTask;
    }
}

internal sealed class FakeValidator(
    string name,
    EventLog events,
    Func<ApplicationValidationContext, ICollection<ApplicationValidationError>, CancellationToken, ValueTask>? operation = null)
    : IApplicationValidator
{
    public ValueTask ValidateAsync(
        ApplicationValidationContext context,
        ICollection<ApplicationValidationError> errors,
        CancellationToken cancellationToken)
    {
        events.Add($"validator.{name}");
        return operation?.Invoke(context, errors, cancellationToken) ?? ValueTask.CompletedTask;
    }
}

internal sealed class FakeParticipant(
    string name,
    ApplicationStartPhase phase,
    EventLog events) : IApplicationStartupParticipant
{
    public ApplicationStartPhase Phase { get; } = phase;

    public Func<CancellationToken, ValueTask>? StartOperation { get; init; }

    public Func<CancellationToken, ValueTask>? StopOperation { get; init; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        events.Add($"participant.{name}.start");
        return StartOperation?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        StopCount++;
        events.Add($"participant.{name}.stop");
        return StopOperation?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }
}

internal sealed class FakeModeRunner(
    LaunchKind kind,
    EventLog events,
    Func<LaunchDecision, CancellationToken, Task<ApplicationRunResult>> operation)
    : IApplicationModeRunner
{
    public LaunchKind Kind { get; } = kind;

    public Task<ApplicationRunResult> RunAsync(
        LaunchDecision decision,
        CancellationToken cancellationToken)
    {
        events.Add("mode.run");
        return operation(decision, cancellationToken);
    }
}

internal sealed class ConstantExitCodePolicy(int exitCode) : IExitCodePolicy
{
    public int GetExitCode(ApplicationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return exitCode;
    }
}
