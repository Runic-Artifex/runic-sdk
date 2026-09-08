using CustomerMigration.Domain;
using Runic.Application.Bridge;
using Runic.Application.Bridge.Generated;

namespace CustomerMigration.After;

public sealed partial class CustomerFeature(CustomerService service, Runic.Platform.IFileDialogs files, Runic.Platform.ITextClipboard clipboard, Runic.Platform.IPlatformCapabilities capabilities)
{
    private readonly object _gate = new();
    private int _generation;
    private SaveState _save = new(null, "idle", 0, "Ready", []);
    [BridgeSnapshot]
    private CustomerSnapshot Snapshot() { lock (_gate) return new(service.Read().Select(ToRow).ToArray(), _save, _generation, _native, NativeCapabilities()); }

    [BridgeCommand]
    private DraftValidated Validate(ValidateCustomer command) => new(CustomerRules.Validate(ToDraft(command.Draft)).Select(ToIssue).ToArray());

    [BridgeCommand]
    private CustomerReloaded Reload(ReloadCustomers command) => new(Snapshot());

    [BridgeCommand(StartsOperation = true, Cancellable = true, AdvancesRevision = true)]
    private SaveStarted Save(SaveCustomer command, BridgeCommandContext context)
    {
        lock (_gate)
        {
            if (_save.Status == "saving") throw new CustomerRejected("Busy", "A save is already running.", []).ToBridgeError();
            var draft = ToDraft(command.Draft);
            var issues = CustomerRules.Validate(draft);
            if (issues.Length > 0) throw new CustomerRejected("Validation", "Correct the highlighted fields.", issues.Select(ToIssue).ToArray()).ToBridgeError();
            var operation = context.Operations.Start((id, token) => SaveAsync(draft, id, context.Events, token));
            SetSave(new(operation.Value, "saving", 0, "Saving customer", []));
            return new(operation.Value, Snapshot());
        }
    }

    private async ValueTask SaveAsync(CustomerDraft draft, BridgeOperationId operation, IBridgeEventPublisher events, CancellationToken token)
    {
        // Start returns before work proceeds; install the operation state before publishing.
        await Task.Yield();
        lock (_gate) { }
        try
        {
            await service.SaveAsync(draft, async percent =>
            {
                lock (_gate) SetSave(new(operation.Value, "saving", percent, "Saving customer", []));
                await events.PublishCustomersChangedAsync(new(Snapshot()), advancesRevision: true, operationId: operation, cancellationToken: token);
            }, token);
            lock (_gate) SetSave(new(operation.Value, "saved", 100, "Customer saved", []));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { lock (_gate) SetSave(new(operation.Value, "cancelled", 0, "Save cancelled; your draft is unchanged", [])); }
        catch (CustomerProblem problem)
        { lock (_gate) SetSave(new(operation.Value, "failed", 0, problem.Message, problem.Issues.Select(ToIssue).ToArray())); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { lock (_gate) SetSave(new(operation.Value, "failed", 0, "Could not write the customer file. Check its location and permissions.", [])); }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            lock (_gate) SetSave(new(operation.Value, "failed", 0, "The save failed unexpectedly. Your draft is still available.", []));
        }
        // Publish even after cancellation; reconnect also reads this terminal state.
        await events.PublishCustomersChangedAsync(new(Snapshot()), advancesRevision: true, operationId: operation, cancellationToken: CancellationToken.None);
    }
    private void SetSave(SaveState state) { _save = state; _generation++; }
    private static CustomerDraft ToDraft(CustomerInput value) => new(value.Id, value.Name, value.Email, value.Company, value.Version);
    private static CustomerRow ToRow(Customer value) => new(value.Id, value.Name, value.Email, value.Company, value.Version);
    private static ValidationIssue ToIssue(FieldIssue value) => new(value.Field, value.Message);
}
public sealed record CustomerRow(Guid Id, string Name, string Email, string Company, int Version);
public sealed record CustomerInput(Guid Id, [property: BridgeStringLength(0, 1000)] string Name, [property: BridgeStringLength(0, 1000)] string Email, [property: BridgeStringLength(0, 1000)] string Company, [property: BridgeMinimum(1)] int Version);
public sealed record ValidationIssue(string Field, string Message);
public sealed record SaveState(Guid? OperationId, string Status, [property: BridgeMinimum(0), BridgeMaximum(100)] int Progress, string Message, ValidationIssue[] Issues);
public sealed record CustomerSnapshot(CustomerRow[] Customers, SaveState Save, int Generation, NativeContactState Native, NativeAvailability Capabilities);
public sealed record ValidateCustomer(CustomerInput Draft);
public sealed record DraftValidated(ValidationIssue[] Issues);
public sealed record ReloadCustomers;
public sealed record CustomerReloaded(CustomerSnapshot Snapshot);
public sealed record SaveCustomer(CustomerInput Draft);
public sealed record SaveStarted(Guid OperationId, CustomerSnapshot Snapshot);
[BridgeEvent] public sealed record CustomersChanged(CustomerSnapshot Snapshot);
[BridgeError] public sealed record CustomerRejected(string Code, string Message, ValidationIssue[] Issues);
