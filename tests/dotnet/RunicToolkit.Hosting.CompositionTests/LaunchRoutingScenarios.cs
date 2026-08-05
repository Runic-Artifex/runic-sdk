using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RunicToolkit.Hosting.CompositionTests;

internal static class LaunchRoutingScenarios
{
    public static ValueTask ClassifiesUserInterface()
    {
        var resolver = new DefaultLaunchIntentResolver();

        LaunchDecision implicitUi = resolver.Resolve(Array.Empty<string>());
        LaunchDecision explicitUi = resolver.Resolve(["--ui"]);

        ContractAssert.Equal(LaunchKind.UserInterface, implicitUi.Kind);
        ContractAssert.Equal(0, implicitUi.Arguments.Count);
        ContractAssert.Equal(LaunchKind.UserInterface, explicitUi.Kind);
        ContractAssert.EqualSequence(["--ui"], explicitUi.Arguments);
        return ValueTask.CompletedTask;
    }

    public static ValueTask ClassifiesHelp()
    {
        var resolver = new DefaultLaunchIntentResolver();
        ContractAssert.Equal(LaunchKind.Help, resolver.Resolve(["--help"]).Kind);
        ContractAssert.Equal(LaunchKind.Help, resolver.Resolve(["-h"]).Kind);
        return ValueTask.CompletedTask;
    }

    public static ValueTask ClassifiesVersion()
    {
        var resolver = new DefaultLaunchIntentResolver();
        LaunchDecision decision = resolver.Resolve(["--version"]);
        ContractAssert.Equal(LaunchKind.Version, decision.Kind);
        ContractAssert.Equal<string?>(null, decision.CommandName);
        return ValueTask.CompletedTask;
    }

    public static ValueTask ClassifiesCommandAndSnapshotsArguments()
    {
        var resolver = new DefaultLaunchIntentResolver();
        string[] arguments = ["serve", "--help"];

        LaunchDecision decision = resolver.Resolve(arguments);
        arguments[0] = "mutated";

        ContractAssert.Equal(LaunchKind.Command, decision.Kind);
        ContractAssert.Equal("serve", decision.CommandName);
        ContractAssert.EqualSequence(["serve", "--help"], decision.Arguments);
        return ValueTask.CompletedTask;
    }

    public static ValueTask RejectsUnknownOption()
    {
        var resolver = new DefaultLaunchIntentResolver();

        LaunchDecision decision = resolver.Resolve(["--Help"]);

        ContractAssert.Equal(LaunchKind.Invalid, decision.Kind);
        ContractAssert.Equal("The launch option is not recognized.", decision.Diagnostic);
        ContractAssert.False(decision.Diagnostic!.Contains("--Help", StringComparison.Ordinal));
        return ValueTask.CompletedTask;
    }

    public static ValueTask RejectsMixedReservedLaunch()
    {
        var resolver = new DefaultLaunchIntentResolver();

        LaunchDecision decision = resolver.Resolve(["--ui", "secret-value"]);

        ContractAssert.Equal(LaunchKind.Invalid, decision.Kind);
        ContractAssert.Equal(
            "A reserved launch option cannot be combined with other arguments.",
            decision.Diagnostic);
        ContractAssert.False(decision.Diagnostic!.Contains("secret-value", StringComparison.Ordinal));
        return ValueTask.CompletedTask;
    }

    public static ValueTask RejectsMissingAndDuplicateRoutes()
    {
        var log = new List<string>();
        var first = new RecordingRunner(LaunchKind.UserInterface, "first", log);
        var second = new RecordingRunner(LaunchKind.Command, "second", log);
        var duplicate = new RecordingRunner(LaunchKind.UserInterface, "duplicate", log);
        var routes = new ApplicationModeRouteTable([first, second, duplicate]);

        ApplicationModeRouteSelection missing = routes.SelectRunner(LaunchKind.Help);
        ApplicationModeRouteSelection duplicates = routes.SelectRunner(LaunchKind.UserInterface);

        ContractAssert.False(missing.IsSuccess);
        ContractAssert.Equal(ApplicationFailureCodes.RunnerSelection, missing.Error!.Code);
        ContractAssert.Equal(0, missing.Error.MatchCount);
        ContractAssert.EqualSequence(Array.Empty<int>(), missing.Error.MatchingRegistrationIndexes);
        ContractAssert.False(duplicates.IsSuccess);
        ContractAssert.Equal(2, duplicates.Error!.MatchCount);
        ContractAssert.EqualSequence([0, 2], duplicates.Error.MatchingRegistrationIndexes);
        ContractAssert.Equal(
            "The selected launch kind must have exactly one mode runner.",
            duplicates.Error.SafeMessage);
        return ValueTask.CompletedTask;
    }

    public static ValueTask RouteTableSnapshotsKindAndRegistrations()
    {
        var log = new List<string>();
        var ui = new RecordingRunner(LaunchKind.UserInterface, "ui", log);
        var registrations = new List<IApplicationModeRunner> { ui };
        var routes = new ApplicationModeRouteTable(registrations);

        registrations.Clear();
        ui.Kind = LaunchKind.Command;

        ApplicationModeRouteSelection selection = routes.SelectRunner(LaunchKind.UserInterface);
        ContractAssert.True(selection.IsSuccess);
        ContractAssert.Same(ui, selection.Runner);
        ContractAssert.Equal(1, routes.Runners.Count);
        return ValueTask.CompletedTask;
    }
}
