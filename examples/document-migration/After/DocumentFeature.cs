using DocumentMigration.Domain;
using Runic.Application.Bridge;
using Runic.Application.Bridge.Generated;
namespace DocumentMigration.After;

public sealed partial class DocumentFeature(DocumentService service)
{
    private readonly object _gate = new();
    private DocumentSnapshot _snapshot = new(null, "idle", 0, 0, null, null, false);
    [BridgeSnapshot] private DocumentSnapshot Snapshot() { lock (_gate) return _snapshot; }
    [BridgeCommand(StartsOperation = true, Cancellable = true, AdvancesRevision = true)]
    private DocumentStarted Open(OpenDocument command, BridgeCommandContext context) => Start("opening", command.Revision, null, context);
    [BridgeCommand(StartsOperation = true, Cancellable = true, AdvancesRevision = true)]
    private DocumentStarted Save(SaveDocument command, BridgeCommandContext context)
    {
        try { DocumentService.Encode(command.Text); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { throw new DocumentRejected("invalid-document", "Use valid UTF-8 text of at most 32 KiB without NUL.").ToBridgeError(); }
        return Start("saving", command.Revision, command.Text, context);
    }
    private DocumentStarted Start(string status, int revision, string? text, BridgeCommandContext context)
    {
        lock (_gate)
        {
            if (_snapshot.Status is "opening" or "saving") throw new DocumentRejected("busy", "Finish or cancel the pending operation.").ToBridgeError();
            var operation = context.Operations.Start((id, token) => Run(status, revision, text, id, context.Events, token));
            _snapshot = new(operation.Value, status, _snapshot.Generation + 1, revision, null, null, false);
            return new(operation.Value, _snapshot);
        }
    }
    private async ValueTask Run(string status, int revision, string? text, BridgeOperationId operation, IBridgeEventPublisher events, CancellationToken token)
    {
        await Task.Yield(); lock (_gate) { }
        DocumentResult result;
        try { result = status == "opening" ? await service.OpenAsync(token) : await service.SaveAsync(text!, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { result = new("cancelled"); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { result = new("invalid-or-inaccessible-document"); }
        catch (Exception error) { Console.Error.WriteLine(error); result = new("failed"); }
        lock (_gate) _snapshot = new(operation.Value, result.Status, _snapshot.Generation + 1, revision, result.Text, result.Name, result.CleanupFailed);
        await events.PublishDocumentChangedAsync(new(Snapshot()), advancesRevision: true, operationId: operation, cancellationToken: CancellationToken.None);
    }
}
public sealed record DocumentSnapshot(Guid? OperationId, string Status, int Generation, int CapturedRevision, string? Text, string? Name, bool CleanupFailed);
public sealed record OpenDocument([property: BridgeMinimum(0)] int Revision);
public sealed record SaveDocument([property: BridgeStringLength(0, 32768)] string Text, [property: BridgeMinimum(0)] int Revision);
public sealed record DocumentStarted(Guid OperationId, DocumentSnapshot Snapshot);
[BridgeEvent] public sealed record DocumentChanged(DocumentSnapshot Snapshot);
[BridgeError] public sealed record DocumentRejected(string Code, string Message);
