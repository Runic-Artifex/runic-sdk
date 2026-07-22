using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting.CompositionTests;

internal static class CompositionScenarios
{
    public static async ValueTask BuilderFreezesComposition()
    {
        var log = new List<string>();
        var host = new RecordingHost(log);
        var runner = new RecordingRunner(LaunchKind.UserInterface, "ui", log);
        var validator = new RecordingValidator("common", log);
        var timeoutSource = new ApplicationTimeoutOptions
        {
            StartupTimeout = TimeSpan.FromSeconds(4),
            TotalShutdownTimeout = TimeSpan.FromSeconds(5),
        };
        var builder = new WebUIToolkitApplicationBuilder()
            .UseHost(host)
            .AddValidator(validator)
            .AddModeRunner(runner)
            .UseTimeouts(timeoutSource);

        WebUIToolkitApplication application = builder.Build();
        timeoutSource.StartupTimeout = TimeSpan.FromMinutes(1);
        ApplicationTimeoutOptions firstCopy = application.Descriptor.Timeouts;
        firstCopy.StartupTimeout = TimeSpan.FromMinutes(2);

        ContractAssert.Equal(TimeSpan.FromSeconds(4), application.Descriptor.Timeouts.StartupTimeout);
        ContractAssert.Equal(1, application.Descriptor.CommonValidators.Count);
        ContractAssert.Equal(1, application.Descriptor.ModeRunners.Count);
        ContractAssert.Equal(LaunchKind.UserInterface, application.Descriptor.ModeRunners[0].Kind);
        ContractAssert.Throws<InvalidOperationException>(() => builder.AddModeRunner(runner));
        ContractAssert.Throws<InvalidOperationException>(() => builder.Build());

        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask ValidatesCommonThenSelectedBeforeStartingHost()
    {
        var log = new List<string>();
        var host = new RecordingHost(log);
        var commonOne = new RecordingValidator("common-1", log);
        var commonTwo = new RecordingValidator("common-2", log);
        var uiOne = new RecordingValidator("ui-1", log);
        var uiTwo = new RecordingValidator("ui-2", log);
        var commandOnly = new RecordingValidator("command", log);
        var uiRunner = new RecordingRunner(LaunchKind.UserInterface, "ui", log);
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(host)
            .AddValidator(commonOne)
            .AddValidator(commonTwo)
            .AddValidator(LaunchKind.UserInterface, uiOne)
            .AddValidator(LaunchKind.UserInterface, uiTwo)
            .AddValidator(LaunchKind.Command, commandOnly)
            .AddModeRunner(uiRunner)
            .Build();

        ApplicationRunResult result = await application.RunAsync().ConfigureAwait(false);

        ContractAssert.True(result.IsSuccess);
        ContractAssert.EqualSequence(
            [
                "validate:common-1:UserInterface",
                "validate:common-2:UserInterface",
                "validate:ui-1:UserInterface",
                "validate:ui-2:UserInterface",
                "host:start",
                "runner:ui:UserInterface",
                "host:stop",
                "host:dispose",
            ],
            log);
        ContractAssert.Equal(0, commandOnly.CallCount);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask DuplicateRouteFailsBeforeHostStart()
    {
        var log = new List<string>();
        var host = new RecordingHost(log);
        var validator = new RecordingValidator("common", log);
        var first = new RecordingRunner(LaunchKind.UserInterface, "first", log);
        var second = new RecordingRunner(LaunchKind.UserInterface, "second", log);
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(host)
            .AddValidator(validator)
            .AddModeRunner(first)
            .AddModeRunner(second)
            .Build();

        ApplicationRunResult result = await application.RunAsync().ConfigureAwait(false);

        ContractAssert.Equal(ApplicationFailureCodes.Validation, result.Failure!.Code);
        ContractAssert.Equal(ApplicationFailureCategory.Configuration, result.Failure.Category);
        ContractAssert.Equal(10, result.ExitCode);
        ContractAssert.Equal(0, host.StartCount);
        ContractAssert.Equal(0, first.CallCount);
        ContractAssert.Equal(0, second.CallCount);
        ContractAssert.Equal(1, validator.CallCount);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask MissingRouteFailsBeforeHostStart()
    {
        var log = new List<string>();
        var host = new RecordingHost(log);
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(host)
            .AddModeRunner(new RecordingRunner(LaunchKind.Command, "command", log))
            .Build();

        ApplicationRunResult result = await application.RunAsync().ConfigureAwait(false);

        ContractAssert.Equal(ApplicationFailureCodes.Validation, result.Failure!.Code);
        ContractAssert.Equal(0, host.StartCount);
        ContractAssert.False(log.Contains("host:start"));
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask RoutesUserInterfaceSuccess()
    {
        var log = new List<string>();
        var ui = new RecordingRunner(LaunchKind.UserInterface, "ui", log);
        var command = new RecordingRunner(LaunchKind.Command, "command", log);
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .AddModeRunner(ui)
            .AddModeRunner(command)
            .Build();

        ApplicationRunResult result = await application.RunAsync(["--ui"]).ConfigureAwait(false);

        ContractAssert.True(result.IsSuccess);
        ContractAssert.Equal(1, ui.CallCount);
        ContractAssert.Equal(0, command.CallCount);
        ContractAssert.Equal(LaunchKind.UserInterface, ui.LastDecision!.Kind);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask RoutesCommandSuccess()
    {
        var log = new List<string>();
        var ui = new RecordingRunner(LaunchKind.UserInterface, "ui", log);
        var command = new RecordingRunner(
            LaunchKind.Command,
            "command",
            log,
            ApplicationRunResult.FromExitCode(7));
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .AddModeRunner(ui)
            .AddModeRunner(command)
            .Build();

        ApplicationRunResult result = await application.RunAsync(["build", "--fast"]).ConfigureAwait(false);

        ContractAssert.Equal(7, result.ExitCode);
        ContractAssert.Equal(0, ui.CallCount);
        ContractAssert.Equal(1, command.CallCount);
        ContractAssert.Equal("build", command.LastDecision!.CommandName);
        ContractAssert.EqualSequence(["build", "--fast"], command.LastDecision.Arguments);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask CancellationPreservesLifecycleSurface()
    {
        var log = new List<string>();
        var host = new RecordingHost(log);
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(host)
            .AddModeRunner(new RecordingRunner(LaunchKind.UserInterface, "ui", log))
            .Build();
        Task<ApplicationRunResult> completionBeforeRun = application.Completion;
        ContractAssert.Equal(ApplicationState.Created, application.State);
        ContractAssert.Same(completionBeforeRun, application.StopController.Completion);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ApplicationRunResult result = await application.RunAsync(cancellation.Token).ConfigureAwait(false);

        ContractAssert.Equal(ApplicationFailureCodes.Cancellation, result.Failure!.Code);
        ContractAssert.Equal(ApplicationFailureCategory.Cancelled, result.Failure.Category);
        ContractAssert.Equal(130, result.ExitCode);
        ContractAssert.Equal(ApplicationState.Faulted, application.State);
        ContractAssert.Same(result, await completionBeforeRun.ConfigureAwait(false));
        ContractAssert.Equal(0, application.SecondaryFailures.Count);
        ContractAssert.Equal(0, host.StartCount);
        await application.DisposeAsync().ConfigureAwait(false);
        ContractAssert.Equal(ApplicationState.Disposed, application.State);
    }

    public static async ValueTask ApplicationIsSingleUseBeforeResolverSideEffects()
    {
        var log = new List<string>();
        var resolver = new RecordingResolver(
            new LaunchDecision(LaunchKind.UserInterface, Array.Empty<string>()),
            log);
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .UseLaunchIntentResolver(resolver)
            .AddModeRunner(new RecordingRunner(LaunchKind.UserInterface, "ui", log))
            .Build();

        await application.RunAsync().ConfigureAwait(false);
        ContractAssert.Throws<InvalidOperationException>(() => application.RunAsync());
        ContractAssert.Equal(1, resolver.CallCount);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask MutableRunnerKindCannotDriftAfterBuild()
    {
        var log = new List<string>();
        var runner = new RecordingRunner(LaunchKind.UserInterface, "mutable", log);
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .AddModeRunner(runner)
            .Build();

        runner.Kind = LaunchKind.Command;
        ApplicationRunResult result = await application.RunAsync(["--ui"]).ConfigureAwait(false);

        ContractAssert.True(result.IsSuccess);
        ContractAssert.Equal(1, runner.CallCount);
        ContractAssert.Equal(LaunchKind.UserInterface, application.Descriptor.ModeRunners[0].Kind);
        ContractAssert.Equal(LaunchKind.UserInterface, runner.LastDecision!.Kind);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask MutableParticipantPhaseCannotDriftAfterBuild()
    {
        var log = new List<string>();
        var first = new RecordingParticipant("first", log)
        {
            Phase = ApplicationStartPhase.Infrastructure,
        };
        var second = new RecordingParticipant("second", log)
        {
            Phase = ApplicationStartPhase.UserInterface,
        };
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .AddStartupParticipant(first)
            .AddStartupParticipant(second)
            .AddModeRunner(new RecordingRunner(LaunchKind.UserInterface, "ui", log))
            .Build();

        first.Phase = ApplicationStartPhase.UserInterface;
        second.Phase = ApplicationStartPhase.Infrastructure;
        ApplicationRunResult result = await application.RunAsync().ConfigureAwait(false);

        ContractAssert.True(result.IsSuccess);
        ContractAssert.EqualSequence(
            [
                "host:start",
                "participant:first:start",
                "participant:second:start",
                "runner:ui:UserInterface",
                "participant:second:stop",
                "participant:first:stop",
                "host:stop",
                "host:dispose",
            ],
            log);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask InvalidArgumentInputDoesNotConsumeApplication()
    {
        var log = new List<string>();
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .AddModeRunner(new RecordingRunner(LaunchKind.UserInterface, "ui", log))
            .Build();

        ContractAssert.Throws<ArgumentNullException>(
            () => application.RunAsync((IReadOnlyList<string>)null!));
        ContractAssert.Throws<ArgumentException>(
            () => application.RunAsync(new string[] { null! }));
        ContractAssert.False(application.Completion.IsCompleted);

        ApplicationRunResult result = await application.RunAsync().ConfigureAwait(false);
        ContractAssert.True(result.IsSuccess);
        await application.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask ResolverFailuresPermitRetryWithoutStrandingCompletion()
    {
        var log = new List<string>();
        LaunchDecision decision = new(LaunchKind.UserInterface, Array.Empty<string>());
        var throwingResolver = new FlakyResolver(
            decision,
            firstException: new InvalidOperationException("resolver-secret"));
        WebUIToolkitApplication throwingApplication = new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .UseLaunchIntentResolver(throwingResolver)
            .AddModeRunner(new RecordingRunner(LaunchKind.UserInterface, "throwing-ui", log))
            .Build();

        ContractAssert.Throws<InvalidOperationException>(() => throwingApplication.RunAsync());
        ContractAssert.False(throwingApplication.Completion.IsCompleted);
        ContractAssert.True((await throwingApplication.RunAsync().ConfigureAwait(false)).IsSuccess);
        ContractAssert.Equal(2, throwingResolver.CallCount);
        await throwingApplication.DisposeAsync().ConfigureAwait(false);

        var nullResolver = new FlakyResolver(decision, returnNullFirst: true);
        WebUIToolkitApplication nullApplication = new WebUIToolkitApplicationBuilder()
            .UseHost(new RecordingHost(log))
            .UseLaunchIntentResolver(nullResolver)
            .AddModeRunner(new RecordingRunner(LaunchKind.UserInterface, "null-ui", log))
            .Build();

        ContractAssert.Throws<InvalidOperationException>(() => nullApplication.RunAsync());
        ContractAssert.False(nullApplication.Completion.IsCompleted);
        ContractAssert.True((await nullApplication.RunAsync().ConfigureAwait(false)).IsSuccess);
        ContractAssert.Equal(2, nullResolver.CallCount);
        await nullApplication.DisposeAsync().ConfigureAwait(false);
    }

    public static async ValueTask DisposeWaitsForResolverHandoff()
    {
        var log = new List<string>();
        var host = new RecordingHost(log);
        using var resolver = new BlockingResolver(
            new LaunchDecision(LaunchKind.UserInterface, Array.Empty<string>()));
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(host)
            .UseLaunchIntentResolver(resolver)
            .AddModeRunner(new RecordingRunner(LaunchKind.UserInterface, "ui", log))
            .Build();

        Task<ApplicationRunResult> runTask = Task.Run(() => application.RunAsync());
        await resolver.Entered.Task.ConfigureAwait(false);
        Task disposeTask = Task.Run(async () =>
            await application.DisposeAsync().ConfigureAwait(false));
        ContractAssert.False(disposeTask.IsCompleted);

        resolver.Release();
        ApplicationRunResult result = await runTask.ConfigureAwait(false);
        await disposeTask.ConfigureAwait(false);

        ContractAssert.Equal(1, resolver.CallCount);
        ContractAssert.Equal(1, host.DisposeCount);
        ContractAssert.Equal(ApplicationState.Disposed, application.State);
        ContractAssert.True(result.ExitCode is 0 or 130);
    }

    public static async ValueTask RejectsMalformedCustomDecisionsBeforeHostStart()
    {
        await AssertDecisionRejectedAsync(
            new LaunchDecision((LaunchKind)999, Array.Empty<string>()),
            LaunchKind.UserInterface).ConfigureAwait(false);
        await AssertDecisionRejectedAsync(
            new LaunchDecision(LaunchKind.Command, ["command"]),
            LaunchKind.Command).ConfigureAwait(false);
        await AssertDecisionRejectedAsync(
            new LaunchDecision(LaunchKind.Command, ["command"], " "),
            LaunchKind.Command).ConfigureAwait(false);
        await AssertDecisionRejectedAsync(
            new LaunchDecision(LaunchKind.Command, ["command"], "bad\rname"),
            LaunchKind.Command).ConfigureAwait(false);
        await AssertDecisionRejectedAsync(
            new LaunchDecision(LaunchKind.UserInterface, Array.Empty<string>(), "unexpected"),
            LaunchKind.UserInterface).ConfigureAwait(false);
    }

    public static ValueTask DefaultResolverRejectsUnsafeCommandTokens()
    {
        var resolver = new DefaultLaunchIntentResolver();
        string[] unsafeTokens = [" ", "bad\rname", "bad\0name"];

        foreach (string unsafeToken in unsafeTokens)
        {
            LaunchDecision decision = resolver.Resolve([unsafeToken, "secret-argument"]);
            ContractAssert.Equal(LaunchKind.Invalid, decision.Kind);
            ContractAssert.True(!string.IsNullOrWhiteSpace(decision.Diagnostic));
            ContractAssert.False(decision.Diagnostic!.Contains("secret-argument", StringComparison.Ordinal));
            ContractAssert.False(decision.Diagnostic.Contains('\r'));
            ContractAssert.False(decision.Diagnostic.Contains('\0'));
            ContractAssert.Equal<string?>(null, decision.CommandName);
        }

        return ValueTask.CompletedTask;
    }

    private static async ValueTask AssertDecisionRejectedAsync(
        LaunchDecision decision,
        LaunchKind registeredKind)
    {
        var log = new List<string>();
        var host = new RecordingHost(log);
        var resolver = new RecordingResolver(decision, log);
        var runner = new RecordingRunner(registeredKind, "runner", log);
        WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(host)
            .UseLaunchIntentResolver(resolver)
            .AddModeRunner(runner)
            .Build();

        ApplicationRunResult result = await application.RunAsync().ConfigureAwait(false);

        ContractAssert.Equal(ApplicationFailureCodes.Validation, result.Failure!.Code);
        ContractAssert.Equal(ApplicationFailureCategory.Configuration, result.Failure.Category);
        ContractAssert.Equal(0, host.StartCount);
        ContractAssert.Equal(0, runner.CallCount);
        await application.DisposeAsync().ConfigureAwait(false);
    }
}
