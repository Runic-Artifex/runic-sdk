using System.Text.Json;

namespace Runic.Application.Views.Generated;

// query-collections/1 (specs/application/README.md, "Query collections"): the rows of a RunicQueryCollection, which each
// connection reads for its own range (query-rows), the query it changes (query-set), and the position of a key
// (query-index). The rows never enter the route's state; its state member carries the query, total and version.
public partial class ViewModelBridge
{
    // The requests a connection may have in flight per query collection: two query-rows (a page sends one at a time), one
    // query-set and one query-index. Further ones answer busy, so one page cannot take every slot of the row source.
    internal const int MaximumQueryRowsPerConnection = 2;

    private ValueTask<ReadOnlyMemory<byte>> Admitted(BridgeRouteRequest request, int limit,
        Func<ValueTask<ReadOnlyMemory<byte>>> run)
    {
        var key = $"{_name}\u0000{request.Member}\u0000{request.Kind}";
        if (!request.Connection.TryEnterQuery(key, limit))
            return new(BridgeMessages.Rejected("busy",
                $"This connection already has {limit} {request.Kind} request{(limit == 1 ? "" : "s")} of {request.Member} in flight."));
        return Run();

        async ValueTask<ReadOnlyMemory<byte>> Run()
        {
            try { return await run().ConfigureAwait(false); }
            finally { request.Connection.ExitQuery(key); }
        }
    }

    private ValueTask<ReadOnlyMemory<byte>> QueryRowsAsync(BridgeRouteRequest request, CancellationToken cancellationToken) =>
        Admitted(request, MaximumQueryRowsPerConnection, () => QueryRowsCoreAsync(request, cancellationToken));

    private ValueTask<ReadOnlyMemory<byte>> QuerySetAsync(BridgeRouteRequest request, CancellationToken cancellationToken) =>
        Admitted(request, 1, () => QuerySetCoreAsync(request, cancellationToken));

    private ValueTask<ReadOnlyMemory<byte>> QueryIndexAsync(BridgeRouteRequest request, CancellationToken cancellationToken) =>
        Admitted(request, 1, () => QueryIndexCoreAsync(request, cancellationToken));

    private async ValueTask<ReadOnlyMemory<byte>> QueryRowsCoreAsync(BridgeRouteRequest request, CancellationToken cancellationToken)
    {
        if (QueryCollection(request, out var property, out var failure) is not { } collection) return failure;
        var start = (int)Math.Min(int.MaxValue, Integer(request.Root, "start"));
        var count = Integer(request.Root, "count");
        if (count > RunicQueryCollectionOptions.MaximumRangeCount)
            return BridgeMessages.Rejected(BridgeFailure.InvalidInput,
                $"A query-rows request reads at most {RunicQueryCollectionOptions.MaximumRangeCount} rows.");
        BridgeQueryRows rows;
        try { rows = await collection.LoadRowsAsync(start, (int)count, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return EncodeFailure(new("cancelled", "The rows request was cancelled."));
        }
        catch (ObjectDisposedException) { return BridgeMessages.Released; }
        // A collection disposed while its load ran cancels the load: the View is gone, not failed.
        catch (Exception) when (collection.IsDisposed || IsInactive) { return BridgeMessages.Released; }
        catch (Exception error)
        {
            ViewsLog.QueryRowsFailed(_logger, error, ModelName, property.Name, _name, start, start + (int)count, BridgeTelemetry.ErrorType(error));
            return EncodeFailure(new("failed", $"The rows of {property.Name} could not be loaded.", BridgeFailureDetails.Capture(error, IncludesFailureDetail)));
        }
        try
        {
            var write = property.QueryRowWriter!;
            return WriteMessage(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", true);
                writer.WriteNumber("version", rows.Version);
                if (rows.Total is { } total) writer.WriteNumber("total", total);
                else writer.WriteNull("total");
                writer.WriteNumber("start", rows.Start);
                writer.WriteStartArray("keys");
                foreach (var row in rows.Rows) writer.WriteStringValue(collection.KeyOf(row));
                writer.WriteEndArray();
                writer.WriteStartArray("rows");
                foreach (var row in rows.Rows) write(writer, row);
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        }
        catch (Exception error)
        {
            ViewsLog.QueryRowsFailed(_logger, error, ModelName, property.Name, _name, start, start + (int)count, BridgeTelemetry.ErrorType(error));
            return EncodeFailure(new("failed", $"The rows of {property.Name} could not be sent.", BridgeFailureDetails.Capture(error, IncludesFailureDetail)));
        }
    }

    private async ValueTask<ReadOnlyMemory<byte>> QuerySetCoreAsync(BridgeRouteRequest request, CancellationToken cancellationToken)
    {
        if (QueryCollection(request, out var property, out var failure) is not { } collection) return failure;
        RunicQuery query;
        try { query = BridgeQueryWire.Read(request.Root.GetProperty("query")); }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            return EncodeFailure(new("rejected", $"The query of {property.Name} is not valid: {error.Message}", Reason: BridgeFailure.InvalidInput));
        }
        var waits = true;
        ReadOnlyMemory<byte> reply;
        try
        {
            reply = _modelTurn.Run<ReadOnlyMemory<byte>>(() =>
            {
                lock (_modelGate)
                {
                    if (IsInactive) return BridgeMessages.Released;
                    try
                    {
                        collection.SetQuery(query);
                        return RanReply(null);
                    }
                    catch (ArgumentException error)
                    {
                        waits = false;
                        return EncodeFailure(new("rejected", $"The query of {property.Name} is not valid: {error.Message}",
                            BridgeFailureDetails.Capture(error, IncludesFailureDetail), Reason: BridgeFailure.InvalidInput));
                    }
                    catch (Exception error)
                    {
                        ViewsLog.QuerySetFailed(_logger, error, ModelName, property.Name, _name, BridgeTelemetry.ErrorType(error));
                        return RanReply(new("failed", $"Could not change the query of {property.Name}.", BridgeFailureDetails.Capture(error, IncludesFailureDetail)));
                    }
                }
            });
        }
        catch (ObjectDisposedException) { return BridgeMessages.Released; }
        if (!waits) return reply;
        await WaitForPublishedAsync(cancellationToken).ConfigureAwait(false);
        return ReleasedIfInactive(reply);
    }

    private async ValueTask<ReadOnlyMemory<byte>> QueryIndexCoreAsync(BridgeRouteRequest request, CancellationToken cancellationToken)
    {
        if (QueryCollection(request, out var property, out var failure) is not { } collection) return failure;
        var key = request.Root.GetProperty("key").GetString()!;
        (long Version, int Index) found;
        try { found = await collection.IndexOfAsync(key, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return EncodeFailure(new("cancelled", "The index request was cancelled."));
        }
        catch (ObjectDisposedException) { return BridgeMessages.Released; }
        catch (Exception) when (collection.IsDisposed || IsInactive) { return BridgeMessages.Released; }
        catch (Exception error)
        {
            ViewsLog.QueryIndexFailed(_logger, error, ModelName, property.Name, _name, BridgeTelemetry.ErrorType(error));
            return EncodeFailure(new("failed", $"The row of {property.Name} could not be found.", BridgeFailureDetails.Capture(error, IncludesFailureDetail)));
        }
        return WriteMessage(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("version", found.Version);
            writer.WriteNumber("index", found.Index);
            writer.WriteEndObject();
        });
    }

    // The query collection a request names, read in a model turn; null with the reply to send instead.
    private IBridgeQueryCollection? QueryCollection(BridgeRouteRequest request, out PropertyDescriptor property,
        out ReadOnlyMemory<byte> failure)
    {
        failure = default;
        if (!_queryCollections.TryGetValue(request.Member!, out property!))
        {
            failure = BridgeMessages.Rejected("invalid-request", $"The route {_name} has no query collection '{request.Member}'.");
            return null;
        }
        var descriptor = property;
        IBridgeQueryCollection? collection;
        try
        {
            collection = _modelTurn.Run(() =>
            {
                lock (_modelGate) return IsInactive ? null : descriptor.Get(_vm) as IBridgeQueryCollection;
            });
        }
        catch (ObjectDisposedException) { collection = null; }
        if (collection is null) failure = IsInactive ? BridgeMessages.Released
            : EncodeFailure(new("failed", $"{property.Name} is null."));
        return collection;
    }
}
