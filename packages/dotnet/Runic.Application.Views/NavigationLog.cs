using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

// Navigation event IDs 1060-1079 (1060-1069 for transitions and cleanup, 1070-1079 for
// PushForResult and later navigation events); 1043-1049 stay reserved for the ReactiveUI
// navigation adapter. Properties carry type and operation names, never values.
[Experimental(RunicNavigator.DiagnosticId)]
internal static partial class NavigationLog
{
    [LoggerMessage(EventId = 1060, EventName = "NavigationGuardFailed", Level = LogLevel.Error,
        Message = "A departure guard of {EntryType} in navigation region {Region} ({RegionId}) failed during {Operation} with {ErrorType}.")]
    internal static partial void NavigationGuardFailed(ILogger logger, Exception? exception, string region, int regionId,
        string operation, string entryType, string errorType);

    [LoggerMessage(EventId = 1061, EventName = "NavigationPreparationFailed", Level = LogLevel.Error,
        Message = "Preparing {EntryType} in navigation region {Region} ({RegionId}) failed during {Operation} with {ErrorType}.")]
    internal static partial void NavigationPreparationFailed(ILogger logger, Exception? exception, string region, int regionId,
        string operation, string entryType, string errorType);

    [LoggerMessage(EventId = 1062, EventName = "NavigationCommitFailed", Level = LogLevel.Error,
        Message = "The commit turn of {Operation} in navigation region {Region} ({RegionId}) could not run: {ErrorType}.")]
    internal static partial void NavigationCommitFailed(ILogger logger, Exception? exception, string region, int regionId,
        string operation, string errorType);

    [LoggerMessage(EventId = 1063, EventName = "NavigationNotificationFailed", Level = LogLevel.Error,
        Message = "A {Property} change handler of navigation region {Region} ({RegionId}) failed with {ErrorType}; the commit stands.")]
    internal static partial void NavigationNotificationFailed(ILogger logger, Exception? exception, string region, int regionId,
        string property, string errorType);

    [LoggerMessage(EventId = 1064, EventName = "NavigationEntryCleanupFailed", Level = LogLevel.Error,
        Message = "Cleanup step {Step} of {EntryType} in navigation region {Region} ({RegionId}) failed with {ErrorType}.")]
    internal static partial void NavigationEntryCleanupFailed(ILogger logger, Exception? exception, string region, int regionId,
        string entryType, string step, string errorType);

    [LoggerMessage(EventId = 1065, EventName = "NavigationTransitionRejected", Level = LogLevel.Debug,
        Message = "{Operation} in navigation region {Region} ({RegionId}) was rejected: {Reason}.")]
    internal static partial void NavigationTransitionRejected(ILogger logger, Exception? exception, string region, int regionId,
        NavigationOperation operation, NavigationRejection reason);

    [LoggerMessage(EventId = 1066, EventName = "NavigationTransitionSuperseded", Level = LogLevel.Debug,
        Message = "{Operation} in navigation region {Region} ({RegionId}) was superseded.")]
    internal static partial void NavigationTransitionSuperseded(ILogger logger, Exception? exception, string region, int regionId,
        NavigationOperation operation);

    [LoggerMessage(EventId = 1067, EventName = "NavigationSupersededTransitionOverrun", Level = LogLevel.Warning,
        Message = "A superseded {Operation} in navigation region {Region} ({RegionId}) is still running 5 seconds after supersession; later requests keep waiting for it.")]
    internal static partial void NavigationSupersededTransitionOverrun(ILogger logger, Exception? exception, string region, int regionId,
        string operation);

    [LoggerMessage(EventId = 1068, EventName = "NavigationCloseTimedOut", Level = LogLevel.Warning,
        Message = "Closing navigation region {Region} ({RegionId}) timed out waiting for a model turn; its state was cleared outside a turn.")]
    internal static partial void NavigationCloseTimedOut(ILogger logger, Exception? exception, string region, int regionId);

    [LoggerMessage(EventId = 1069, EventName = "NavigationInitializeTimedOut", Level = LogLevel.Warning,
        Message = "Retiring {EntryType} in navigation region {Region} ({RegionId}) stopped waiting for its initialize hook after the close timeout; the content is disposed while the hook runs.")]
    internal static partial void NavigationInitializeTimedOut(ILogger logger, Exception? exception, string region, int regionId, string entryType);

    [LoggerMessage(EventId = 1070, EventName = "NavigationResultDismissed", Level = LogLevel.Debug,
        Message = "A PushForResult request in navigation region {Region} ({RegionId}) was dismissed: {Reason}.")]
    internal static partial void NavigationResultDismissed(ILogger logger, Exception? exception, string region, int regionId, NavigationResultDismissal reason);

    [LoggerMessage(EventId = 1071, EventName = "NavigationResultDropped", Level = LogLevel.Debug,
        Message = "An entry of navigation region {Region} ({RegionId}) completed a PushForResult request that was already dismissed; it went back and the result was dropped.")]
    internal static partial void NavigationResultDropped(ILogger logger, Exception? exception, string region, int regionId);
}
