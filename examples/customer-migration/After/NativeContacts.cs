using CustomerMigration.Domain;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Runic.Application.Bridge;
using Runic.Application.Bridge.Generated;
using Runic.Platform;

namespace CustomerMigration.After;

public sealed partial class CustomerFeature
{
    private NativeContactState _native = new(null, "idle", "Ready", null, 0, null);
    private NativeAvailability NativeCapabilities()
    {
        var statuses = capabilities.GetSnapshot().Statuses;
        bool Has(string key) => statuses.TryGetValue(key, out var value) && value is CapabilityStatus.Available;
        return new(Has("platform.files.open"), Has("platform.files.save"), Has("platform.clipboard.readText"), Has("platform.clipboard.writeText"));
    }

    [BridgeCommand(StartsOperation = true, Cancellable = true, AdvancesRevision = true)]
    private NativeContactStarted TransferContact(TransferContact command, BridgeCommandContext context)
    {
        lock (_gate)
        {
            if (_native.Status == "running") throw new CustomerRejected("Busy", "A native operation is already running.", []).ToBridgeError();
            if (command.Action is not ("import" or "export" or "copy" or "paste"))
                throw new CustomerRejected("Validation", "Unknown contact action.", []).ToBridgeError();
            // Export and copy capture an explicitly confirmed persisted revision, never a changing frontend draft.
            ContactData? captured = null;
            if (command.Action is "export" or "copy")
            {
                var row = service.Read().SingleOrDefault(row => row.Id == command.CustomerId && row.Version == command.Version)
                    ?? throw new CustomerRejected("Conflict", "This saved customer changed. Reload and confirm its revision again.", []).ToBridgeError();
                captured = new(row.Name, row.Email, row.Company);
            }
            var operation = context.Operations.Start((id, token) => TransferAsync(command, captured, id, context.Events, token));
            SetNative(new(operation.Value, "running", $"Contact {command.Action} in progress", null, command.DraftSequence, command.CustomerId));
            return new(operation.Value, Snapshot());
        }
    }

    private async ValueTask TransferAsync(TransferContact command, ContactData? captured, BridgeOperationId operation, IBridgeEventPublisher events, CancellationToken token)
    {
        await Task.Yield();
        lock (_gate) { }
        string status = "completed", message = "Contact operation completed";
        ContactData? candidate = null;
        bool nativeWriteOutcomeKnown = false;
        bool cleanupFailed = false;
        try
        {
            if (command.Action == "import")
            {
                var result = await files.OpenFileAsync(new(), token);
                if (result is PickerResult<IReadFileLease>.Selected selected)
                {
                    await using var lease = selected.Value;
                    await using var stream = await lease.OpenReadAsync(token);
                    byte[] bytes = new byte[4097];
                    int count = 0, read;
                    while (count < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(count), token)) != 0) count += read;
                    if (count > 4096) throw new InvalidDataException("Contact JSON exceeds 4096 bytes.");
                    candidate = ContactCodec.Parse(new UTF8Encoding(false, true).GetString(bytes, 0, count));
                    message = "Contact ready to review";
                }
                else (status, message) = PickerOutcome(result);
            }
            else if (command.Action == "paste")
            {
                var result = await clipboard.ReadTextAsync(4096, token);
                if (result is PlatformResult<string?>.Success success)
                {
                    if (success.Value is null) { status = "no-text"; message = "The clipboard contains no text"; }
                    else { candidate = ContactCodec.Parse(success.Value); message = "Clipboard contact ready to review"; }
                }
                else (status, message) = PlatformOutcome(result);
            }
            else
            {
                string text = ContactCodec.Serialize(captured!);
                if (command.Action == "copy")
                {
                    var result = await clipboard.WriteTextAsync(text, token);
                    nativeWriteOutcomeKnown = true;
                    if (result is PlatformResult<Unit>.Success) message = $"Copied saved revision {command.Version}";
                    else (status, message) = PlatformOutcome(result);
                }
                else
                {
                    var result = await files.SaveFileAsync(new("contact.json"), token);
                    if (result is PickerResult<ISaveFileLease>.Selected selected)
                    {
                        await using var lease = selected.Value;
                        var begin = await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace, token);
                        if (begin is PlatformResult<IFileWriteTransaction>.Success success)
                        {
                            await using var transaction = success.Value;
                            await transaction.Content.WriteAsync(Encoding.UTF8.GetBytes(text), token);
                            // Once the provider begins committing, preserve its actual result, including uncertainty.
                            var commit = await transaction.CommitAsync(token);
                            nativeWriteOutcomeKnown = true;
                            (status, message) = commit switch
                            {
                                FileCommitResult.Committed => ("completed", $"Exported saved revision {command.Version}"),
                                FileCommitResult.NotCommitted failed => ("failed", $"Export was not committed: {failed.Code}"),
                                FileCommitResult.CommitUnknown unknown => ("uncertain", $"Export outcome is uncertain ({unknown.Code}). Inspect the destination before retrying."),
                                _ => throw new InvalidOperationException(),
                            };
                        }
                        else (status, message) = PlatformOutcome(begin);
                    }
                    else (status, message) = PickerOutcome(result);
                }
            }
            // Read operations have no irreversible outcome. Cancellation during asynchronous
            // lease cleanup must not publish a candidate as a completed import.
            if (command.Action is "import" or "paste") token.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (nativeWriteOutcomeKnown)
        {
            // Cleanup failure cannot turn an acknowledged write into a cancellation or retryable failure.
            Console.Error.WriteLine(error);
            cleanupFailed = true;
            message += "; cleanup reported an error. Inspect the destination before retrying.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { status = "cancelled"; message = "Contact operation cancelled"; }
        catch (Exception error) when (error is JsonException or InvalidDataException or DecoderFallbackException)
        { status = "failed"; message = "Choose valid contact JSON with name, email and company text fields, at most 4096 UTF-8 bytes."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { status = "failed"; message = "Contact access failed. Check permissions and try again."; }
        catch (Exception error) { Console.Error.WriteLine(error); status = "failed"; message = "Contact operation failed unexpectedly."; }
        if (status != "completed") candidate = null;
        lock (_gate) SetNative(new(operation.Value, status, message, candidate, command.DraftSequence, command.CustomerId, cleanupFailed));
        await events.PublishCustomersChangedAsync(new(Snapshot()), advancesRevision: true, operationId: operation, cancellationToken: CancellationToken.None);
    }
    private void SetNative(NativeContactState state) { _native = state; _generation++; }
    private static (string, string) PickerOutcome<T>(PickerResult<T> result) where T : IAsyncDisposable => result switch
    {
        PickerResult<T>.Dismissed => ("dismissed", "No file selected"),
        PickerResult<T>.Unavailable value => ("unavailable", $"Native file selection is unavailable: {value.Reason}"),
        PickerResult<T>.Failed value => ("failed", $"Native file selection failed: {value.Code}"),
        _ => throw new InvalidOperationException(),
    };
    private static (string, string) PlatformOutcome<T>(PlatformResult<T> result) => result switch
    {
        PlatformResult<T>.Unavailable value => ("unavailable", $"Native service is unavailable: {value.Reason}"),
        PlatformResult<T>.Failed value => ("failed", $"Native operation failed: {value.Code}"),
        _ => throw new InvalidOperationException(),
    };
}

public sealed record NativeAvailability(bool Open, bool Save, bool Paste, bool Copy);
public sealed record NativeContactState(Guid? OperationId, string Status, string Message, ContactData? Candidate, int DraftSequence, Guid? CustomerId, bool CleanupFailed = false);
public sealed record TransferContact([property: BridgeStringLength(1, 20)] string Action, Guid CustomerId, [property: BridgeMinimum(1)] int Version, [property: BridgeMinimum(0)] int DraftSequence);
public sealed record NativeContactStarted(Guid OperationId, CustomerSnapshot Snapshot);
