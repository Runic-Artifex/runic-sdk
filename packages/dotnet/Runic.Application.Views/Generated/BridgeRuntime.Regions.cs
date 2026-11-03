using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Runic.Navigation;

namespace Runic.Application.Views.Generated;

/// <summary>Helpers that generated bridges use to write region collections (<see cref="RunicRegionCollectionAttribute"/>).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class BridgeRegionCollections
{
    /// <summary>
    /// The key of a region in a region collection: a decimal integer, unique among the regions of the region's navigator
    /// and never reused by it, so it identifies the region for as long as the window lives.
    /// </summary>
    /// <param name="region">A region of the collection.</param>
    /// <returns>The region's key.</returns>
    public static string Key(NavigationRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return region.Core.Id.ToString(CultureInfo.InvariantCulture);
    }

    // The region of the selection that a region request's key names, or null.
    internal static NavigationRegion? Find(NavigationSelection selection, string key)
    {
        foreach (var region in selection.Regions)
            if (string.Equals(Key(region), key, StringComparison.Ordinal)) return region;
        return null;
    }

    // NavigationOutcome as its wire value (specs/application/README.md, "Region collections"): enum members by name.
    internal static void WriteOutcome(Utf8JsonWriter writer, NavigationOutcome outcome)
    {
        writer.WritePropertyName("outcome");
        writer.WriteStartObject();
        writer.WriteString("kind", outcome.Kind.ToString());
        if (outcome.Rejection is { } rejection) writer.WriteString("rejection", rejection.ToString());
        else writer.WriteNull("rejection");
        writer.WriteString("arrival", outcome.Arrival.ToString());
        writer.WriteEndObject();
    }
}

// The region request of a Bridge's route (region-collections/1, specs/application/README.md, "Region collections"):
// select or close the region of a region collection that a key names. It runs NavigationSelection.SelectAsync or
// RemoveAsync, so the departure guards of the region left or closed decide, and replies with the request's outcome after
// the route published the state that includes it.
public partial class ViewModelBridge<T>
{
    private async ValueTask<ReadOnlyMemory<byte>> RegionAsync(BridgeRouteRequest request, CancellationToken token)
    {
        var root = request.Root;
        var member = request.Envelope.Member!;
        var close = root.GetProperty("action").GetString() == "close";
        var key = root.GetProperty("key").GetString()!;
        var descriptor = Array.Find(_properties, property => property.RegionCollection && property.WireName == member);
        if (descriptor is null)
            return BridgeMessages.Rejected("invalid-request", $"The route has no region collection '{member}'.");
        if (close && !descriptor.RegionClose)
            return EncodeFailure(new("rejected", $"{descriptor.Name} does not let the page close a region.", Reason: BridgeFailure.Unavailable));

        // A region request is window work like an awaited command: it counts toward the window's awaited calls, closing
        // the window waits for it, and the close timeout cancels it.
        var atCapacity = false;
        using var admission = _content is null ? null : _content.Operations.TryBeginAwaited(token, out atCapacity);
        if (_content is not null && admission is null)
            return atCapacity
                ? EncodeFailure(new("rejected",
                    $"{descriptor.Name} was not changed: the window is already running {BridgeOperationRegistry.MaximumAwaitedExecutions} awaited calls.",
                    Reason: "capacity"))
                : EncodeFailure(new(BridgeFailure.Released, "The window is closing."));
        var cancellation = admission?.Token ?? token;
        ValueTask<NavigationResult<object>> started = default;
        ReadOnlyMemory<byte>? refused;
        try
        {
            refused = _modelTurn.Run<ReadOnlyMemory<byte>?>(() =>
            {
                lock (_modelGate)
                {
                    if (IsInactive) return BridgeMessages.Released;
                    if (descriptor.Observe?.Invoke(_vm) is not NavigationSelection selection
                        || BridgeRegionCollections.Find(selection, key) is not { } region)
                    {
                        ViewsLog.RegionRequestUnresolved(_logger, ModelName, descriptor.Name, _name, key);
                        return EncodeFailure(new("rejected", $"{descriptor.Name} has no region with this key.", Reason: BridgeFailure.UnresolvedKey));
                    }
                    started = close
                        ? selection.RemoveAsync(region, cancellationToken: cancellation)
                        : selection.SelectAsync(region, cancellationToken: cancellation);
                    return null;
                }
            });
        }
        catch (ObjectDisposedException) { return BridgeMessages.Released; }
        catch (Exception error)
        {
            ViewsLog.RegionRequestFailed(_logger, error, ModelName, descriptor.Name, _name, BridgeTelemetry.ErrorType(error));
            return EncodeFailure(new("failed", $"{descriptor.Name} could not be changed.", BridgeFailureDetails.Capture(error, IncludesFailureDetail)));
        }
        if (refused is { } answer) return answer;

        NavigationOutcome outcome;
        BridgeFailure? failure = null;
        try { outcome = (await started.ConfigureAwait(false)).Outcome; }
        catch (OperationCanceledException)
        {
            outcome = new(NavigationOutcomeKind.Rejected, NavigationRejection.Cancelled);
        }
        catch (Exception error)
        {
            ViewsLog.RegionRequestFailed(_logger, error, ModelName, descriptor.Name, _name, BridgeTelemetry.ErrorType(error));
            outcome = new(NavigationOutcomeKind.Failed);
            failure = new("failed", $"{descriptor.Name} could not be changed.", BridgeFailureDetails.Capture(error, IncludesFailureDetail));
        }
        Action<Utf8JsonWriter>? writeOutcome = failure is null ? writer => BridgeRegionCollections.WriteOutcome(writer, outcome) : null;
        ReadOnlyMemory<byte> reply;
        try
        {
            reply = _modelTurn.Run(() =>
            {
                // A close can release the route that asked for it; that reply still carries the outcome.
                lock (_modelGate)
                    return IsInactive ? EncodeRanReply(failure, revision: null, released: true, writeOutcome) : RanReply(failure, writeOutcome);
            });
        }
        catch (ObjectDisposedException)
        {
            return EncodeRanReply(failure, revision: null, released: true, writeOutcome);
        }
        await WaitForPublishedAsync(token).ConfigureAwait(false);
        return ReleasedIfInactive(reply);
    }
}
