using System.Collections.Generic;

namespace RunicToolkit.Hosting.CompositionTests;

internal static class ScenarioCatalog
{
    public static IReadOnlyList<ContractScenario> All { get; } =
    [
        new("launch-ui", LaunchRoutingScenarios.ClassifiesUserInterface),
        new("launch-help", LaunchRoutingScenarios.ClassifiesHelp),
        new("launch-version", LaunchRoutingScenarios.ClassifiesVersion),
        new("launch-command-snapshot", LaunchRoutingScenarios.ClassifiesCommandAndSnapshotsArguments),
        new("launch-unknown-option", LaunchRoutingScenarios.RejectsUnknownOption),
        new("launch-mixed-reserved", LaunchRoutingScenarios.RejectsMixedReservedLaunch),
        new("routes-missing-duplicate", LaunchRoutingScenarios.RejectsMissingAndDuplicateRoutes),
        new("routes-immutable-snapshot", LaunchRoutingScenarios.RouteTableSnapshotsKindAndRegistrations),
        new("builder-immutable-freeze", CompositionScenarios.BuilderFreezesComposition),
        new("composition-validation-order", CompositionScenarios.ValidatesCommonThenSelectedBeforeStartingHost),
        new("composition-duplicate-route", CompositionScenarios.DuplicateRouteFailsBeforeHostStart),
        new("composition-missing-route", CompositionScenarios.MissingRouteFailsBeforeHostStart),
        new("composition-ui-success", CompositionScenarios.RoutesUserInterfaceSuccess),
        new("composition-command-success", CompositionScenarios.RoutesCommandSuccess),
        new("composition-cancellation-lifecycle", CompositionScenarios.CancellationPreservesLifecycleSurface),
        new("composition-single-use", CompositionScenarios.ApplicationIsSingleUseBeforeResolverSideEffects),
        new("composition-runner-kind-freeze", CompositionScenarios.MutableRunnerKindCannotDriftAfterBuild),
        new("composition-participant-phase-freeze", CompositionScenarios.MutableParticipantPhaseCannotDriftAfterBuild),
        new("composition-invalid-input-retry", CompositionScenarios.InvalidArgumentInputDoesNotConsumeApplication),
        new("composition-resolver-failure-retry", CompositionScenarios.ResolverFailuresPermitRetryWithoutStrandingCompletion),
        new("composition-dispose-resolver-race", CompositionScenarios.DisposeWaitsForResolverHandoff),
        new("composition-malformed-decision-shape", CompositionScenarios.RejectsMalformedCustomDecisionsBeforeHostStart),
        new("launch-unsafe-command-token", CompositionScenarios.DefaultResolverRejectsUnsafeCommandTokens),
        new("events-success-order", LifecycleEventScenarios.PublishesStableSuccessOrder),
        new("events-sanitization", LifecycleEventScenarios.SanitizesFailureEvents),
        new("events-sink-isolation", LifecycleEventScenarios.SinkFailuresCannotChangeLifecycle),
        new("events-unsafe-code-sanitization", LifecycleEventScenarios.UnsafeFailureCodeIsRemoved),
        new("events-primary-before-policy-secondary", LifecycleEventScenarios.PrimaryFailurePrecedesExitPolicyFailure),
        new("events-reentrant-dispose", LifecycleEventScenarios.CompletionSinkCanDisposeReentrantly),
        new("browser-safe-identifier-grammar", BrowserContractScenarios.EnforcesSafeIdentifierGrammar),
        new("events-stop-before-shutdown-deadline", LifecycleEventScenarios.StopPublicationPrecedesShutdownDeadline),
        new("events-stop-reason-terminal-outcome", LifecycleEventScenarios.StopReasonReflectsTerminalOutcome),
    ];
}
