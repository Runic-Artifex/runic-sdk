using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.ContractTests.Fakes;

namespace WebUIToolkit.Hosting.ContractTests;

internal static partial class ScenarioCatalog
{
    private static partial IReadOnlyList<ContractScenario> Create() =>
    [
        new("state-machine.legal-transition-graph", LegalTransitionGraphAsync),
        new("state-machine.illegal-edge-preserves-state", IllegalTransitionPreservesStateAsync),
        new("lifecycle.legal-order-and-disposal", LegalLifecycleAsync),
        new("lifecycle.invalid-intent-does-not-start", InvalidIntentAsync),
        new("lifecycle.external-cancellation", ExternalCancellationAsync),
        new("lifecycle.partial-start-reverse-stop", PartialStartFailureAsync),
        new("lifecycle.first-failure-precedence", FirstFailurePrecedenceAsync),
        new("lifecycle.startup-timeout-manual-time", StartupTimeoutAsync),
        new("lifecycle.stop-timeout-manual-time", StopTimeoutAsync),
        new("lifecycle.total-shutdown-timeout-manual-time", TotalShutdownTimeoutAsync),
        new("lifecycle.total-deadline-caps-active-mode-and-teardown", TotalDeadlineCapsActiveModeAsync),
        new("lifecycle.concurrent-stop-first-signal-wins", FirstStopSignalWinsAsync),
        new("lifecycle.throwing-stop-callback-does-not-break-completion", ThrowingStopCallbackAsync),
        new("lifecycle.dispose-without-run-is-idempotent", DisposeWithoutRunAsync),
        new("failure.kernel-generated-ids-are-stable", StableKernelFailuresAsync),
        new("failure.default-exit-codes-are-stable", StableExitCodesAsync),
    ];

    private static ValueTask LegalTransitionGraphAsync()
    {
        ApplicationLifecycleStateMachine stateMachine = new();

        ContractAssert.Equal(ApplicationState.Created, stateMachine.State);
        stateMachine.Transition(ApplicationState.Validating);
        stateMachine.Transition(ApplicationState.Starting);
        stateMachine.Transition(ApplicationState.Running);
        stateMachine.Transition(ApplicationState.Stopping);
        stateMachine.Transition(ApplicationState.Stopped);
        stateMachine.Transition(ApplicationState.Disposed);
        ContractAssert.Equal(ApplicationState.Disposed, stateMachine.State);

        ApplicationLifecycleStateMachine faulted = new();
        faulted.Transition(ApplicationState.Stopping);
        faulted.Transition(ApplicationState.Faulted);
        faulted.Transition(ApplicationState.Disposed);
        ContractAssert.Equal(ApplicationState.Disposed, faulted.State);
        return ValueTask.CompletedTask;
    }

    private static async ValueTask IllegalTransitionPreservesStateAsync()
    {
        ApplicationLifecycleStateMachine stateMachine = new();

        ContractAssert.False(stateMachine.TryTransition(ApplicationState.Running));
        ContractAssert.Equal(ApplicationState.Created, stateMachine.State);
        InvalidOperationException exception = await ContractAssert.ThrowsAsync<InvalidOperationException>(
            () =>
            {
                stateMachine.Transition(ApplicationState.Disposed);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        ContractAssert.Equal(
            "Illegal lifecycle transition: Created -> Disposed.",
            exception.Message);
        ContractAssert.Equal(ApplicationState.Created, stateMachine.State);

        stateMachine.Transition(ApplicationState.Validating);
        ContractAssert.False(stateMachine.TryTransition(ApplicationState.Disposed));
        ContractAssert.Equal(ApplicationState.Validating, stateMachine.State);
    }

    private static async ValueTask LegalLifecycleAsync()
    {
        EventLog events = new();
        FakeApplicationHost host = new(events);
        FakeValidator firstValidator = new("first", events);
        FakeValidator secondValidator = new("second", events);
        FakeParticipant userInterface = new("ui", ApplicationStartPhase.UserInterface, events);
        FakeParticipant infrastructure = new("infra", ApplicationStartPhase.Infrastructure, events);
        FakeParticipant integration = new("integration", ApplicationStartPhase.Integrations, events);
        FakeModeRunner runner = SuccessRunner(events);
        ApplicationLifecycleKernel kernel = CreateKernel(
            host,
            [firstValidator, secondValidator],
            [userInterface, infrastructure, integration],
            [runner]);

        ApplicationRunResult result = await kernel.RunAsync(UserInterfaceDecision()).ConfigureAwait(false);

        ContractAssert.True(result.IsSuccess);
        ContractAssert.Equal(0, result.ExitCode!.Value);
        ContractAssert.True(result.Failure is null);
        ContractAssert.Equal(ApplicationState.Stopped, kernel.State);
        ContractAssert.True(ReferenceEquals(result, await kernel.Completion.ConfigureAwait(false)));
        ContractAssert.EqualSequence(
            new[]
            {
                "validator.first",
                "validator.second",
                "host.start",
                "participant.infra.start",
                "participant.integration.start",
                "participant.ui.start",
                "mode.run",
                "participant.ui.stop",
                "participant.integration.stop",
                "participant.infra.stop",
                "host.stop",
                "host.dispose",
            },
            events.Snapshot());

        await kernel.DisposeAsync().ConfigureAwait(false);
        await kernel.DisposeAsync().ConfigureAwait(false);
        ContractAssert.Equal(ApplicationState.Disposed, kernel.State);
        ContractAssert.Equal(1, host.DisposeCount);

        _ = await ContractAssert.ThrowsAsync<InvalidOperationException>(
            () => new ValueTask(kernel.RunAsync(UserInterfaceDecision())),
            "a completed lifecycle is single-use").ConfigureAwait(false);
    }

    private static async ValueTask InvalidIntentAsync()
    {
        EventLog events = new();
        FakeApplicationHost host = new(events);
        FakeModeRunner runner = SuccessRunner(events);
        ApplicationLifecycleKernel kernel = CreateKernel(host, [], [], [runner]);

        ApplicationRunResult result = await kernel.RunAsync(
            new LaunchDecision(LaunchKind.Invalid, Array.Empty<string>(), diagnostic: "bad input"))
            .ConfigureAwait(false);

        AssertFailure(result, ApplicationFailureCategory.Usage, "WUTHOST1001", 2);
        ContractAssert.Equal(ApplicationState.Faulted, kernel.State);
        ContractAssert.Equal(0, host.StartCount);
        ContractAssert.Equal(0, host.StopCount);
        ContractAssert.Equal(1, host.DisposeCount);
        ContractAssert.EqualSequence(new[] { "host.dispose" }, events.Snapshot());

        await kernel.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask ExternalCancellationAsync()
    {
        EventLog events = new();
        TaskCompletionSource<object?> entered = NewSignal();
        FakeApplicationHost host = new(events);
        FakeParticipant participant = new("infra", ApplicationStartPhase.Infrastructure, events);
        FakeModeRunner runner = new(
            LaunchKind.UserInterface,
            events,
            (_, token) => WaitForCancellationAsync(entered, token));
        ApplicationLifecycleKernel kernel = CreateKernel(host, [], [participant], [runner]);
        using CancellationTokenSource cancellation = new();

        Task<ApplicationRunResult> run = kernel.RunAsync(UserInterfaceDecision(), cancellation.Token);
        await entered.Task.ConfigureAwait(false);
        ContractAssert.Equal(ApplicationState.Running, kernel.State);
        cancellation.Cancel();
        ApplicationRunResult result = await run.ConfigureAwait(false);

        AssertFailure(result, ApplicationFailureCategory.Cancelled, "WUTHOST1301", 130);
        ContractAssert.EqualSequence(
            new[]
            {
                "host.start",
                "participant.infra.start",
                "mode.run",
                "participant.infra.stop",
                "host.stop",
                "host.dispose",
            },
            events.Snapshot());

        await kernel.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask PartialStartFailureAsync()
    {
        EventLog events = new();
        FakeApplicationHost host = new(events);
        FakeParticipant first = new("first", ApplicationStartPhase.Infrastructure, events);
        FakeParticipant failing = new("failing", ApplicationStartPhase.Integrations, events)
        {
            StartOperation = _ => ValueTask.FromException(new ExpectedTestException("start failed")),
        };
        FakeParticipant neverStarted = new("never", ApplicationStartPhase.UserInterface, events);
        ApplicationLifecycleKernel kernel = CreateKernel(
            host,
            [],
            [neverStarted, failing, first],
            [SuccessRunner(events)]);

        ApplicationRunResult result = await kernel.RunAsync(UserInterfaceDecision()).ConfigureAwait(false);

        AssertFailure(result, ApplicationFailureCategory.HostStartup, "WUTHOST1102", 11);
        ContractAssert.Equal(1, first.StartCount);
        ContractAssert.Equal(1, first.StopCount);
        ContractAssert.Equal(1, failing.StartCount);
        ContractAssert.Equal(0, failing.StopCount);
        ContractAssert.Equal(0, neverStarted.StartCount);
        ContractAssert.EqualSequence(
            new[]
            {
                "host.start",
                "participant.first.start",
                "participant.failing.start",
                "participant.first.stop",
                "host.stop",
                "host.dispose",
            },
            events.Snapshot());

        await kernel.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask FirstFailurePrecedenceAsync()
    {
        EventLog events = new();
        FakeApplicationHost host = new(events)
        {
            StopOperation = _ => ValueTask.FromException(new ExpectedTestException("host stop failed")),
            DisposeOperation = () => ValueTask.FromException(new ExpectedTestException("dispose failed")),
        };
        FakeParticipant participant = new("infra", ApplicationStartPhase.Infrastructure, events)
        {
            StopOperation = _ => ValueTask.FromException(new ExpectedTestException("participant stop failed")),
        };
        ApplicationFailure primary = new(
            ApplicationFailureCategory.Command,
            "APP-COMMAND-0001",
            "The command failed.");
        FakeModeRunner runner = new(
            LaunchKind.Command,
            events,
            (_, _) => Task.FromResult(ApplicationRunResult.FromFailure(primary)));
        ApplicationLifecycleKernel kernel = CreateKernel(host, [], [participant], [runner]);

        ApplicationRunResult result = await kernel.RunAsync(CommandDecision()).ConfigureAwait(false);

        AssertFailure(result, ApplicationFailureCategory.Command, "APP-COMMAND-0001", 20);
        ContractAssert.EqualSequence(
            new[] { "WUTHOST1401", "WUTHOST1403", "WUTHOST1404" },
            kernel.SecondaryFailures.Select(static failure => failure.Code).ToArray());
        ContractAssert.EqualSequence(
            new[]
            {
                "host.start",
                "participant.infra.start",
                "mode.run",
                "participant.infra.stop",
                "host.stop",
                "host.dispose",
            },
            events.Snapshot());

        await kernel.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask StartupTimeoutAsync()
    {
        EventLog events = new();
        ManualTimeProvider time = new();
        TaskCompletionSource<object?> hostStartEntered = NewSignal();
        TaskCompletionSource<object?> neverStarts = NewSignal();
        FakeApplicationHost host = new(events)
        {
            StartOperation = _ =>
            {
                hostStartEntered.TrySetResult(null);
                return new ValueTask(neverStarts.Task);
            },
        };
        ApplicationTimeoutOptions options = TestTimeouts(startup: TimeSpan.FromSeconds(5));
        ApplicationLifecycleKernel kernel = CreateKernel(
            host,
            [],
            [],
            [SuccessRunner(events)],
            time,
            options);

        Task<ApplicationRunResult> run = kernel.RunAsync(UserInterfaceDecision());
        await hostStartEntered.Task.ConfigureAwait(false);
        ContractAssert.NotCompleted(run, "the injected clock has not reached the startup deadline");
        time.Advance(TimeSpan.FromSeconds(5));
        ApplicationRunResult result = await run.ConfigureAwait(false);

        AssertFailure(result, ApplicationFailureCategory.HostStartup, "WUTHOST1103", 11);
        ContractAssert.EqualSequence(
            new[] { "host.start", "host.stop", "host.dispose" },
            events.Snapshot());

        await kernel.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask StopTimeoutAsync()
    {
        EventLog events = new();
        ManualTimeProvider time = new();
        TaskCompletionSource<object?> stopEntered = NewSignal();
        TaskCompletionSource<object?> neverStops = NewSignal();
        FakeApplicationHost host = new(events);
        FakeParticipant participant = new("infra", ApplicationStartPhase.Infrastructure, events)
        {
            StopOperation = _ =>
            {
                stopEntered.TrySetResult(null);
                return new ValueTask(neverStops.Task);
            },
        };
        ApplicationFailure primary = new(
            ApplicationFailureCategory.Command,
            "APP-COMMAND-0002",
            "The command failed before shutdown.");
        FakeModeRunner runner = new(
            LaunchKind.Command,
            events,
            (_, _) => Task.FromResult(ApplicationRunResult.FromFailure(primary)));
        ApplicationLifecycleKernel kernel = CreateKernel(
            host,
            [],
            [participant],
            [runner],
            time,
            TestTimeouts(participantStop: TimeSpan.FromSeconds(3)));

        Task<ApplicationRunResult> run = kernel.RunAsync(CommandDecision());
        await stopEntered.Task.ConfigureAwait(false);
        ContractAssert.NotCompleted(run, "participant teardown is waiting on the manual timeout");
        time.Advance(TimeSpan.FromSeconds(3));
        ApplicationRunResult result = await run.ConfigureAwait(false);

        AssertFailure(result, ApplicationFailureCategory.Command, "APP-COMMAND-0002", 20);
        ContractAssert.Equal(1, kernel.SecondaryFailures.Count);
        ContractAssert.Equal("WUTHOST1402", kernel.SecondaryFailures[0].Code);
        ContractAssert.EqualSequence(
            new[]
            {
                "host.start",
                "participant.infra.start",
                "mode.run",
                "participant.infra.stop",
                "host.stop",
                "host.dispose",
            },
            events.Snapshot());

        await kernel.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask TotalShutdownTimeoutAsync()
    {
        EventLog events = new();
        ManualTimeProvider time = new();
        TaskCompletionSource<object?> stopEntered = NewSignal();
        TaskCompletionSource<object?> neverStops = NewSignal();
        FakeApplicationHost host = new(events);
        FakeParticipant participant = new("infra", ApplicationStartPhase.Infrastructure, events)
        {
            StopOperation = _ =>
            {
                stopEntered.TrySetResult(null);
                return new ValueTask(neverStops.Task);
            },
        };
        ApplicationLifecycleKernel kernel = CreateKernel(
            host,
            [],
            [participant],
            [SuccessRunner(events)],
            time,
            TestTimeouts(
                participantStop: TimeSpan.FromSeconds(10),
                totalShutdown: TimeSpan.FromSeconds(2)));

        Task<ApplicationRunResult> run = kernel.RunAsync(UserInterfaceDecision());
        await stopEntered.Task.ConfigureAwait(false);
        ContractAssert.NotCompleted(run);
        time.Advance(TimeSpan.FromSeconds(2));
        ApplicationRunResult result = await run.ConfigureAwait(false);

        AssertFailure(result, ApplicationFailureCategory.Shutdown, "WUTHOST1405", 40);
        ContractAssert.EqualSequence(
            new[]
            {
                "host.start",
                "participant.infra.start",
                "mode.run",
                "participant.infra.stop",
                "host.stop",
                "host.dispose",
            },
            events.Snapshot());

        await kernel.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask TotalDeadlineCapsActiveModeAsync()
    {
        EventLog events = new();
        ManualTimeProvider time = new();
        TaskCompletionSource<object?> runnerEntered = NewSignal();
        TaskCompletionSource<object?> runnerNeverStops = NewSignal();
        TaskCompletionSource<object?> participantNeverStops = NewSignal();
        bool participantReceivedCancellation = false;
        FakeApplicationHost host = new(events);
        FakeParticipant participant = new("infra", ApplicationStartPhase.Infrastructure, events)
        {
            StopOperation = token =>
            {
                participantReceivedCancellation = token.IsCancellationRequested;
                return new ValueTask(participantNeverStops.Task);
            },
        };
        FakeModeRunner runner = new(
            LaunchKind.UserInterface,
            events,
            (_, _) =>
            {
                runnerEntered.TrySetResult(null);
                return AwaitRunResultAsync(runnerNeverStops.Task);
            });
        ApplicationLifecycleKernel kernel = CreateKernel(
            host,
            [],
            [participant],
            [runner],
            time,
            TestTimeouts(totalShutdown: TimeSpan.FromSeconds(2)));

        Task<ApplicationRunResult> run = kernel.RunAsync(UserInterfaceDecision());
        await runnerEntered.Task.ConfigureAwait(false);
        ContractAssert.True(kernel.StopController.RequestStop(StopReason.ApplicationRequested));
        ContractAssert.NotCompleted(run, "the cancellation-ignoring mode is bounded by manual time");
        time.Advance(TimeSpan.FromSeconds(2));
        ApplicationRunResult result = await run.ConfigureAwait(false);

        AssertFailure(result, ApplicationFailureCategory.Shutdown, "WUTHOST1405", 40);
        ContractAssert.True(
            participantReceivedCancellation,
            "the exhausted total deadline cancels remaining participant teardown before invocation");
        ContractAssert.Equal(1, kernel.SecondaryFailures.Count);
        ContractAssert.Equal("WUTHOST1405", kernel.SecondaryFailures[0].Code);
        ContractAssert.EqualSequence(
            new[]
            {
                "host.start",
                "participant.infra.start",
                "mode.run",
                "participant.infra.stop",
                "host.stop",
                "host.dispose",
            },
            events.Snapshot());

        await kernel.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask FirstStopSignalWinsAsync()
    {
        EventLog events = new();
        TaskCompletionSource<object?> entered = NewSignal();
        FakeApplicationHost host = new(events);
        FakeModeRunner runner = new(
            LaunchKind.UserInterface,
            events,
            (_, token) => WaitForCancellationAsync(entered, token));
        ApplicationLifecycleKernel kernel = CreateKernel(host, [], [], [runner]);

        Task<ApplicationRunResult> run = kernel.RunAsync(UserInterfaceDecision());
        await entered.Task.ConfigureAwait(false);
        ContractAssert.True(kernel.StopController.RequestStop(default));
        ContractAssert.False(kernel.StopController.RequestStop(default));
        ApplicationRunResult result = await run.ConfigureAwait(false);

        ContractAssert.True(result.IsSuccess);
        ContractAssert.Equal(0, result.ExitCode!.Value);
        ContractAssert.Completed(kernel.StopController.Completion);
        ContractAssert.True(ReferenceEquals(kernel.Completion, kernel.StopController.Completion));

        await kernel.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask ThrowingStopCallbackAsync()
    {
        EventLog events = new();
        TaskCompletionSource<object?> runnerEntered = NewSignal();
        TaskCompletionSource<object?> callbackInvoked = NewSignal();
        TaskCompletionSource<ApplicationRunResult> runnerCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeApplicationHost host = new(events);
        FakeParticipant participant = new("infra", ApplicationStartPhase.Infrastructure, events);
        FakeModeRunner runner = new(
            LaunchKind.UserInterface,
            events,
            (_, token) =>
            {
                CancellationTokenRegistration registration = token.Register(() =>
                {
                    callbackInvoked.TrySetResult(null);
                    runnerCompleted.TrySetResult(ApplicationRunResult.FromExitCode(0));
                    throw new ExpectedTestException("consumer cancellation callback failed");
                });
                runnerEntered.TrySetResult(null);
                return AwaitRegisteredResultAsync(runnerCompleted.Task, registration);
            });
        ApplicationLifecycleKernel kernel = CreateKernel(host, [], [participant], [runner]);

        Task<ApplicationRunResult> run = kernel.RunAsync(UserInterfaceDecision());
        await runnerEntered.Task.ConfigureAwait(false);
        ContractAssert.True(kernel.StopController.RequestStop(StopReason.ApplicationRequested));
        await callbackInvoked.Task.ConfigureAwait(false);
        ApplicationRunResult result = await run.ConfigureAwait(false);

        ContractAssert.True(result.IsSuccess);
        ContractAssert.Completed(kernel.Completion);
        ContractAssert.Equal(ApplicationState.Stopped, kernel.State);
        ContractAssert.EqualSequence(
            new[]
            {
                "host.start",
                "participant.infra.start",
                "mode.run",
                "participant.infra.stop",
                "host.stop",
                "host.dispose",
            },
            events.Snapshot());

        await kernel.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask DisposeWithoutRunAsync()
    {
        EventLog events = new();
        FakeApplicationHost host = new(events);
        ApplicationLifecycleKernel kernel = CreateKernel(host, [], [], [SuccessRunner(events)]);

        await kernel.DisposeAsync().ConfigureAwait(false);
        await kernel.DisposeAsync().ConfigureAwait(false);

        ContractAssert.Equal(ApplicationState.Disposed, kernel.State);
        ContractAssert.Equal(0, host.StartCount);
        ContractAssert.Equal(0, host.StopCount);
        ContractAssert.Equal(1, host.DisposeCount);
        ContractAssert.EqualSequence(new[] { "host.dispose" }, events.Snapshot());
        _ = await ContractAssert.ThrowsAsync<InvalidOperationException>(
            () => new ValueTask(kernel.RunAsync(UserInterfaceDecision())))
            .ConfigureAwait(false);
    }

    private static async ValueTask StableKernelFailuresAsync()
    {
        EventLog validationEvents = new();
        FakeApplicationHost validationHost = new(validationEvents);
        FakeValidator first = new(
            "first",
            validationEvents,
            (_, errors, _) =>
            {
                errors.Add(new ApplicationValidationError("APP-VALIDATION-1", "First error."));
                return ValueTask.CompletedTask;
            });
        FakeValidator second = new(
            "second",
            validationEvents,
            (_, errors, _) =>
            {
                errors.Add(new ApplicationValidationError("APP-VALIDATION-2", "Second error."));
                return ValueTask.CompletedTask;
            });
        ApplicationLifecycleKernel validationKernel = CreateKernel(
            validationHost,
            [first, second],
            [],
            [SuccessRunner(validationEvents)]);
        ApplicationRunResult validation = await validationKernel.RunAsync(UserInterfaceDecision()).ConfigureAwait(false);
        AssertFailure(validation, ApplicationFailureCategory.Configuration, "WUTHOST1001", 10);
        ContractAssert.EqualSequence(
            new[] { "validator.first", "validator.second", "host.dispose" },
            validationEvents.Snapshot());
        await validationKernel.DisposeAsync().ConfigureAwait(false);

        EventLog startEvents = new();
        FakeApplicationHost startHost = new(startEvents)
        {
            StartOperation = _ => ValueTask.FromException(new ExpectedTestException("host start failed")),
        };
        ApplicationLifecycleKernel startKernel = CreateKernel(
            startHost,
            [],
            [],
            [SuccessRunner(startEvents)]);
        ApplicationRunResult start = await startKernel.RunAsync(UserInterfaceDecision()).ConfigureAwait(false);
        AssertFailure(start, ApplicationFailureCategory.HostStartup, "WUTHOST1101", 11);
        await startKernel.DisposeAsync().ConfigureAwait(false);

        EventLog selectionEvents = new();
        ApplicationLifecycleKernel selectionKernel = CreateKernel(
            new FakeApplicationHost(selectionEvents),
            [],
            [],
            []);
        ApplicationRunResult selection = await selectionKernel.RunAsync(UserInterfaceDecision()).ConfigureAwait(false);
        AssertFailure(selection, ApplicationFailureCategory.Configuration, "WUTHOST1201", 10);
        await selectionKernel.DisposeAsync().ConfigureAwait(false);

        EventLog runnerEvents = new();
        FakeModeRunner throwingRunner = new(
            LaunchKind.UserInterface,
            runnerEvents,
            (_, _) => Task.FromException<ApplicationRunResult>(new ExpectedTestException("mode failed")));
        ApplicationLifecycleKernel runnerKernel = CreateKernel(
            new FakeApplicationHost(runnerEvents),
            [],
            [],
            [throwingRunner]);
        ApplicationRunResult runner = await runnerKernel.RunAsync(UserInterfaceDecision()).ConfigureAwait(false);
        AssertFailure(runner, ApplicationFailureCategory.UserInterface, "WUTHOST1202", 30);
        await runnerKernel.DisposeAsync().ConfigureAwait(false);
    }

    private static ValueTask StableExitCodesAsync()
    {
        DefaultExitCodePolicy policy = new();
        (ApplicationFailureCategory Category, int ExitCode)[] expected =
        [
            (ApplicationFailureCategory.Usage, 2),
            (ApplicationFailureCategory.Configuration, 10),
            (ApplicationFailureCategory.HostStartup, 11),
            (ApplicationFailureCategory.FrontendAssets, 12),
            (ApplicationFailureCategory.NativeRuntime, 13),
            (ApplicationFailureCategory.Command, 20),
            (ApplicationFailureCategory.UserInterface, 30),
            (ApplicationFailureCategory.Shutdown, 40),
            (ApplicationFailureCategory.Cancelled, 130),
            (ApplicationFailureCategory.Unhandled, 1),
        ];

        foreach ((ApplicationFailureCategory category, int exitCode) in expected)
        {
            ApplicationFailure failure = new(category, "WUTHOST9999", "Safe test failure.");
            ContractAssert.Equal(exitCode, policy.GetExitCode(failure));
        }

        ConstantExitCodePolicy custom = new(73);
        ContractAssert.Equal(
            73,
            custom.GetExitCode(new ApplicationFailure(
                ApplicationFailureCategory.Unhandled,
                "WUTHOST9999",
                "Safe test failure.")));
        return ValueTask.CompletedTask;
    }

    private static ApplicationLifecycleKernel CreateKernel(
        FakeApplicationHost host,
        IEnumerable<IApplicationValidator> validators,
        IEnumerable<IApplicationStartupParticipant> participants,
        IEnumerable<IApplicationModeRunner> runners,
        TimeProvider? timeProvider = null,
        ApplicationTimeoutOptions? timeouts = null)
    {
        ApplicationLifecycleDescriptor descriptor = new(
            host,
            validators,
            participants,
            runners,
            timeouts: timeouts);
        return new ApplicationLifecycleKernel(descriptor, timeProvider);
    }

    private static FakeModeRunner SuccessRunner(EventLog events) => new(
        LaunchKind.UserInterface,
        events,
        (_, _) => Task.FromResult(ApplicationRunResult.FromExitCode(0)));

    private static LaunchDecision UserInterfaceDecision() =>
        new(LaunchKind.UserInterface, Array.Empty<string>());

    private static LaunchDecision CommandDecision() =>
        new(LaunchKind.Command, ["build"], commandName: "build");

    private static ApplicationTimeoutOptions TestTimeouts(
        TimeSpan? startup = null,
        TimeSpan? participantStop = null,
        TimeSpan? totalShutdown = null) =>
        new()
        {
            StartupTimeout = startup ?? TimeSpan.FromSeconds(30),
            ParticipantStopTimeout = participantStop ?? TimeSpan.FromSeconds(30),
            SessionCloseTimeout = TimeSpan.FromSeconds(30),
            WindowCloseTimeout = TimeSpan.FromSeconds(30),
            HostStopTimeout = TimeSpan.FromSeconds(30),
            TotalShutdownTimeout = totalShutdown ?? TimeSpan.FromMinutes(2),
        };

    private static async Task<ApplicationRunResult> WaitForCancellationAsync(
        TaskCompletionSource<object?> entered,
        CancellationToken cancellationToken)
    {
        entered.TrySetResult(null);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("An infinite cancellable delay completed without cancellation.");
    }

    private static async Task<ApplicationRunResult> AwaitRunResultAsync(Task task)
    {
        await task.ConfigureAwait(false);
        return ApplicationRunResult.FromExitCode(0);
    }

    private static async Task<ApplicationRunResult> AwaitRegisteredResultAsync(
        Task<ApplicationRunResult> task,
        CancellationTokenRegistration registration)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
        }
    }

    private static TaskCompletionSource<object?> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertFailure(
        ApplicationRunResult result,
        ApplicationFailureCategory category,
        string code,
        int exitCode)
    {
        ContractAssert.False(result.IsSuccess);
        ContractAssert.Equal(exitCode, result.ExitCode!.Value);
        ApplicationFailure failure = ContractAssert.IsType<ApplicationFailure>(result.Failure);
        ContractAssert.Equal(category, failure.Category);
        ContractAssert.Equal(code, failure.Code);
    }
}

internal sealed class ExpectedTestException(string message) : Exception(message);
