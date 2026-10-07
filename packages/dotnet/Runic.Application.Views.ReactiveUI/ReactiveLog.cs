// Shared by Runic.Application.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.ReactiveUI.Reactive.
using Microsoft.Extensions.Logging;

#if SYSTEM_REACTIVE
namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
namespace Runic.Application.Views.ReactiveUI;
#endif

// Event IDs continue the Views table and are documented in the Views README
// (Logging and telemetry). The category is RunicViewsTelemetry.LogCategory.
internal static partial class ReactiveLog
{
    [LoggerMessage(EventId = 1040, EventName = "RoutedRegionRouteIncompatible", Level = LogLevel.Error,
        Message = "A routed region for {Region} received {Model}, which it cannot present; the region presents no content.")]
    internal static partial void RouteIncompatible(ILogger logger, string region, string model);

    [LoggerMessage(EventId = 1041, EventName = "RoutedRegionRouterFailed", Level = LogLevel.Error,
        Message = "The router of a routed region for {Region} failed with {ErrorType}; the region keeps its last content.")]
    internal static partial void RouterFailed(ILogger logger, Exception? exception, string region, string errorType);
}
