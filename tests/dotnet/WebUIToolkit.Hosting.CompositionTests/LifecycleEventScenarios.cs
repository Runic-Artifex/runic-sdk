using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting.CompositionTests;

internal static class LifecycleEventScenarios
{
    private static readonly DateTimeOffset FixedNow =
        new(2035, 6, 7, 8, 9, 10, TimeSpan.Zero);

    public static async ValueTask PublishesStableSuccessOrder()
    {
        var log = new List<string>();
        var sink = new RecordingEventSink();
        WebUIToolkitApplication application = CreateUserInterfaceApplication(log, sink);

        ApplicationRunResult result = await application.RunAsync().ConfigureAwait(false);
        await sink.WaitForEventAsync<ApplicationCompletionEvent>().ConfigureAwait(false);

        ContractAssert.True(result.IsSuccess);
        ContractAssert.EqualSequence(
            [
                ApplicationLifecycleEventIds.LaunchSelected,
                ApplicationLifecycleEventIds.StateTransition,
                ApplicationLifecycleEventIds.StateTransition,
                ApplicationLifecycleEventIds.StateTransition,
                ApplicationLifecycleEventIds.StopRequested,
                ApplicationLifecycleEventIds.StateTransition,
                ApplicationLifecycleEventIds.StateTransition,
                ApplicationLifecycleEventIds.Completion,
            ],
            sink.Events.Select(lifecycleEvent => lifecycleEvent.EventId).ToArray());

        for (var index = 0; index < sink.Events.Count; index++)
        {
            ContractAssert.Equal(index + 1L, sink.Events[index].Sequence);
            ContractAssert.Equal(FixedNow, sink.Events[index].Timestamp);
        }

        var launch = ContractAssert.IsType<ApplicationLaunchEvent>(sink.Events[0]);
        ContractAssert.Equal(LaunchKind.UserInterface, launch.LaunchKind);
        var stop = ContractAssert.IsType<ApplicationStopRequestedEvent>(sink.Events[4]);
        ContractAssert.Equal(StopReason.ModeCompleted, stop.Reason);
        ContractAssert.Equal(1, sink.Events.OfType<ApplicationStopRequestedEvent>().Count());
        var completion = ContractAssert.IsType<ApplicationCompletionEvent>(sink.Events[^1]);
        ContractAssert.Equal(0, completion.ExitCode);
        ContractAssert.True(completion.IsSuccess);
        ContractAssert.Equal(0, completion.SecondaryFailureCount);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask SanitizesFailureEvents()
    {
        const string secret = "secret-launch-value";
        var log = new List<string>();
        var sink = new RecordingEventSink();
        WebUIToolkitApplication application = CreateUserInterfaceApplication(log, sink);

        ApplicationRunResult result = await application
            .RunAsync([$"--unknown-{secret}"])
            .ConfigureAwait(false);
        await sink.WaitForEventAsync<ApplicationCompletionEvent>().ConfigureAwait(false);

        ContractAssert.Equal(ApplicationFailureCodes.Validation, result.Failure!.Code);
        ApplicationFailureEvent failure = sink.Events
            .OfType<ApplicationFailureEvent>()
            .Single(lifecycleFailure => lifecycleFailure.IsPrimary);
        ContractAssert.Equal(ApplicationFailureCategory.Usage, failure.Category);
        ContractAssert.Equal(ApplicationFailureCodes.Validation, failure.FailureCode);
        ContractAssert.True(failure.IsExpected);

        string eventText = string.Join("|", sink.Events.Select(static lifecycleEvent => lifecycleEvent.ToString()));
        ContractAssert.False(eventText.Contains(secret, StringComparison.Ordinal));
        string[] forbiddenProperties = ["Arguments", "CommandName", "Diagnostic", "SafeMessage", "Exception"];
        foreach (ApplicationLifecycleEvent lifecycleEvent in sink.Events)
        {
            PropertyInfo[] properties = lifecycleEvent.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
            foreach (string forbiddenProperty in forbiddenProperties)
            {
                ContractAssert.False(
                    properties.Any(property => property.Name == forbiddenProperty),
                    $"structured event {lifecycleEvent.GetType().Name} exposed {forbiddenProperty}");
            }
        }

        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask SinkFailuresCannotChangeLifecycle()
    {
        var log = new List<string>();
        var sink = new RecordingEventSink(throwOnPublish: true);
        WebUIToolkitApplication application = CreateUserInterfaceApplication(log, sink);

        ApplicationRunResult result = await application.RunAsync().ConfigureAwait(false);
        await sink.WaitForEventAsync<ApplicationCompletionEvent>().ConfigureAwait(false);

        ContractAssert.True(result.IsSuccess);
        ContractAssert.Equal(ApplicationState.Stopped, application.State);
        ContractAssert.Equal(8, sink.Events.Count);
        ContractAssert.Equal(0, application.SecondaryFailures.Count);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask UnsafeFailureCodeIsRemoved()
    {
        const string secretCode = "SECRET1234\r\nforged event";
        var log = new List<string>();
        var sink = new RecordingEventSink();
        var runner = new RecordingRunner(
            LaunchKind.UserInterface,
            "unsafe-failure",
            log,
            ApplicationRunResult.FromFailure(new ApplicationFailure(
                ApplicationFailureCategory.UserInterface,
                secretCode,
                "secret message",
                new InvalidOperationException("secret exception"),
                IsExpected: false)));
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .AddModeRunner(runner)
            .UseTimeProvider(new FixedTimeProvider(FixedNow))
            .UseLifecycleEventSink(sink)
            .Build();

        ApplicationRunResult result = await application.RunAsync().ConfigureAwait(false);
        await sink.WaitForEventAsync<ApplicationCompletionEvent>().ConfigureAwait(false);

        ContractAssert.Equal(30, result.ExitCode);
        ApplicationFailureEvent failure = sink.Events.OfType<ApplicationFailureEvent>().Single();
        ContractAssert.Equal<string?>(null, failure.FailureCode);
        string eventText = string.Join("|", sink.Events.Select(static lifecycleEvent => lifecycleEvent.ToString()));
        ContractAssert.False(eventText.Contains("SECRET1234", StringComparison.Ordinal));
        ContractAssert.False(eventText.Contains("secret message", StringComparison.Ordinal));
        ContractAssert.False(eventText.Contains("secret exception", StringComparison.Ordinal));
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask PrimaryFailurePrecedesExitPolicyFailure()
    {
        var log = new List<string>();
        var sink = new RecordingEventSink();
        var runner = new RecordingRunner(
            LaunchKind.UserInterface,
            "failure",
            log,
            ApplicationRunResult.FromFailure(new ApplicationFailure(
                ApplicationFailureCategory.UserInterface,
                "WUTHOST7777",
                "safe failure")));
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .AddModeRunner(runner)
            .UseExitCodePolicy(new ThrowingExitCodePolicy())
            .UseTimeProvider(new FixedTimeProvider(FixedNow))
            .UseLifecycleEventSink(sink)
            .Build();

        ApplicationRunResult result = await application.RunAsync().ConfigureAwait(false);
        await sink.WaitForEventAsync<ApplicationCompletionEvent>().ConfigureAwait(false);

        ContractAssert.Equal(1, result.ExitCode);
        ApplicationFailureEvent[] failures = sink.Events.OfType<ApplicationFailureEvent>().ToArray();
        ContractAssert.Equal(2, failures.Length);
        ContractAssert.True(failures[0].IsPrimary);
        ContractAssert.Equal("WUTHOST7777", failures[0].FailureCode);
        ContractAssert.False(failures[1].IsPrimary);
        ContractAssert.Equal(ApplicationFailureCodes.RunnerFailure, failures[1].FailureCode);
        ContractAssert.True(failures[0].Sequence < failures[1].Sequence);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask CompletionSinkCanDisposeReentrantly()
    {
        var log = new List<string>();
        var sink = new ReentrantDisposeSink();
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .AddModeRunner(new RecordingRunner(LaunchKind.UserInterface, "ui", log))
            .UseTimeProvider(new FixedTimeProvider(FixedNow))
            .UseLifecycleEventSink(sink)
            .Build();
        sink.Application = application;

        ApplicationRunResult result = await application.RunAsync().ConfigureAwait(false);
        await sink.CompletionObserved.ConfigureAwait(false);

        ContractAssert.True(result.IsSuccess);
        ContractAssert.Equal(1, sink.Events.OfType<ApplicationCompletionEvent>().Count());
        ContractAssert.Equal(ApplicationState.Disposed, application.State);
        ContractAssert.True(application.Completion.IsCompletedSuccessfully);
    }

    public static async ValueTask StopPublicationPrecedesShutdownDeadline()
    {
        var log = new List<string>();
        var timeProvider = new ManualTimeProvider();
        var sink = new StopAdvancingEventSink(timeProvider, TimeSpan.FromMinutes(1));
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .AddModeRunner(new RecordingRunner(LaunchKind.UserInterface, "ui", log))
            .ConfigureTimeouts(timeouts =>
                timeouts.TotalShutdownTimeout = TimeSpan.FromSeconds(5))
            .UseTimeProvider(timeProvider)
            .UseLifecycleEventSink(sink)
            .Build();

        ApplicationRunResult result = await application.RunAsync().ConfigureAwait(false);
        await sink.CompletionObserved.ConfigureAwait(false);

        ContractAssert.True(result.IsSuccess);
        ContractAssert.Equal(ApplicationState.Stopped, application.State);
        ContractAssert.Equal(0, sink.Events.OfType<ApplicationTimeoutEvent>().Count());
        ContractAssert.Equal(1, sink.Events.OfType<ApplicationStopRequestedEvent>().Count());
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask StopReasonReflectsTerminalOutcome()
    {
        StopReason returnedFailure = await RunAndGetStopReasonAsync(
            new RecordingRunner(
                LaunchKind.UserInterface,
                "returned-failure",
                new List<string>(),
                ApplicationRunResult.FromFailure(new ApplicationFailure(
                    ApplicationFailureCategory.UserInterface,
                    "WUTHOST7777",
                    "Runner returned a failure.")))).ConfigureAwait(false);

        StopReason thrownFailure = await RunAndGetStopReasonAsync(
            new RecordingRunner(
                LaunchKind.UserInterface,
                "thrown-failure",
                new List<string>())
            {
                Handler = static (_, _) => Task.FromException<ApplicationRunResult>(
                    new InvalidOperationException("runner-secret")),
            }).ConfigureAwait(false);

        StopReason nonzeroCompletion = await RunAndGetStopReasonAsync(
            new RecordingRunner(
                LaunchKind.UserInterface,
                "nonzero",
                new List<string>(),
                ApplicationRunResult.FromExitCode(7))).ConfigureAwait(false);

        ContractAssert.Equal(StopReason.FatalFailure, returnedFailure);
        ContractAssert.Equal(StopReason.FatalFailure, thrownFailure);
        ContractAssert.Equal(StopReason.ModeCompleted, nonzeroCompletion);
    }

    private static WebUIToolkitApplication CreateUserInterfaceApplication(
        ICollection<string> log,
        IApplicationLifecycleEventSink sink) =>
        new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .AddModeRunner(new RecordingRunner(LaunchKind.UserInterface, "ui", log))
            .UseTimeProvider(new FixedTimeProvider(FixedNow))
            .UseLifecycleEventSink(sink)
            .Build();

    private static async ValueTask<StopReason> RunAndGetStopReasonAsync(
        IApplicationModeRunner runner)
    {
        var log = new List<string>();
        var sink = new RecordingEventSink();
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .AddModeRunner(runner)
            .UseTimeProvider(new FixedTimeProvider(FixedNow))
            .UseLifecycleEventSink(sink)
            .Build();

        await application.RunAsync().ConfigureAwait(false);
        await sink.WaitForEventAsync<ApplicationCompletionEvent>().ConfigureAwait(false);
        StopReason reason = sink.Events.OfType<ApplicationStopRequestedEvent>().Single().Reason;
        await application.DisposeAsync().ConfigureAwait(false);
        return reason;
    }
}
