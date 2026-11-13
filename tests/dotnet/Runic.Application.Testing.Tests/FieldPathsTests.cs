using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// 0.16 C4 (field-paths/1): checked writes below a member, through the generated Bridges of both flavors and the test
// driver's WritePathAsync.
internal sealed class FieldPathsTests
{
    private static RunicWindowTestHost<PathInvoiceViewModel> Toolkit(PathInvoiceViewModel model) =>
        new(model, (transport, content, vm) => new PathInvoiceBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "pathInvoice", ViewLocator = new TestViewLocator() });

    [Test]
    public async Task ACellWriteRunsTheRowEditorAndReportsTheCellAsync()
    {
        var model = new PathInvoiceViewModel();
        using var host = Toolkit(model);
        var receipt = await host.Root.WritePathAsync(vm => vm.Lines, ["2", "quantity"], 3);
        Require(receipt.Kind == RunicFieldWriteReceiptKind.Applied && receipt.Value?.GetInt32() == 3, receipt.Json);
        Require(model.Lines[1] == new PathLine(2, "Ink", 3, 4.00m) && model.Total == 15.00m, $"{model.Lines[1]} {model.Total}");
        Require(receipt.Version == receipt.State!.Revision, $"The receipt's version is not the reply's revision: {receipt.Json}");

        // The editor's normalization is the receipt's value.
        var product = await host.Root.WritePathAsync(vm => vm.Lines, ["1", "product"], "  Pencils ");
        Require(product.Kind == RunicFieldWriteReceiptKind.Applied && product.Value?.GetString() == "Pencils", product.Json);

        // The editor refuses a value: the row is unchanged and the receipt carries its message.
        var negative = await host.Root.WritePathAsync(vm => vm.Lines, ["1", "price"], "-1");
        Require(negative.Kind == RunicFieldWriteReceiptKind.Rejected && negative.ErrorMessage == PathInvoiceViewModel.NegativePrice
            && model.Lines[0].Price == 1.50m, negative.Json);
    }

    [Test]
    public async Task ACellThatChangedMeanwhileIsAConflictButItsNeighbourIsNotAsync()
    {
        var model = new PathInvoiceViewModel();
        using var host = Toolkit(model);
        var before = (await host.Root.SnapshotAsync()).Revision;
        // Another writer changes the quantity of line 1.
        await host.Root.WritePathAsync(vm => vm.Lines, ["1", "quantity"], 5);
        var stale = await host.Root.WritePathAsync(vm => vm.Lines, ["1", "quantity"], 7, expectedVersion: before);
        Require(stale.Kind == RunicFieldWriteReceiptKind.Conflict && stale.Value?.GetInt32() == 5 && model.Lines[0].Quantity == 5, stale.Json);
        // The product of the same row did not change, so a write based on the older state applies.
        var neighbour = await host.Root.WritePathAsync(vm => vm.Lines, ["1", "product"], "Markers", expectedVersion: before);
        Require(neighbour.Kind == RunicFieldWriteReceiptKind.Applied && model.Lines[0] == new PathLine(1, "Markers", 5, 1.50m), neighbour.Json);
        // A retry from the conflict's version applies.
        var retried = await host.Root.WritePathAsync(vm => vm.Lines, ["1", "quantity"], 7, expectedVersion: stale.Version);
        Require(retried.Kind == RunicFieldWriteReceiptKind.Applied && model.Lines[0].Quantity == 7, retried.Json);
    }

    [Test]
    public async Task AValueThatChangedBackIsStillAConflictAsync()
    {
        var model = new PathInvoiceViewModel();
        using var host = Toolkit(model);
        var before = (await host.Root.SnapshotAsync()).Revision;
        await host.Root.WritePathAsync(vm => vm.Lines, ["2", "quantity"], 9);
        await host.Root.WritePathAsync(vm => vm.Lines, ["2", "quantity"], 1);
        var stale = await host.Root.WritePathAsync(vm => vm.Lines, ["2", "quantity"], 4, expectedVersion: before);
        Require(stale.Kind == RunicFieldWriteReceiptKind.Conflict && stale.Value?.GetInt32() == 1, stale.Json);
    }

    [Test]
    public async Task ANestedMemberIsWrittenThroughTheSetterAsync()
    {
        var model = new PathInvoiceViewModel();
        using var host = Toolkit(model);
        var street = await host.Root.WritePathAsync(vm => vm.Address, ["street"], "Main Street 1");
        Require(street.Kind == RunicFieldWriteReceiptKind.Applied && model.Address == new PathAddress("Main Street 1", "Bern"), street.Json);
        // The member's own checked field follows: its version advanced.
        var state = await host.Root.SnapshotAsync();
        Require(state.FieldVersion(vm => vm.Address) == 1, $"The address version is {state.FieldVersion(vm => vm.Address)}.");

        // An object on the path is null: the write is rejected, and nothing changes.
        var lat = await host.Root.WritePathAsync(vm => vm.Address, ["geo", "lat"], 46.95);
        Require(lat.Kind == RunicFieldWriteReceiptKind.Rejected && model.Address.Geo is null, lat.Json);
        model.Address = model.Address with { Geo = new(0, 7.44) };
        var set = await host.Root.WritePathAsync(vm => vm.Address, ["geo", "lat"], 46.95);
        Require(set.Kind == RunicFieldWriteReceiptKind.Applied && model.Address.Geo == new PathGeo(46.95, 7.44), set.Json);
    }

    [Test]
    public async Task CellErrorsAreReportedAtTheRowsIndexAsync()
    {
        var model = new PathInvoiceViewModel();
        using var host = Toolkit(model);
        var receipt = await host.Root.WritePathAsync(vm => vm.Lines, ["2", "quantity"], 0);
        Require(receipt.Kind == RunicFieldWriteReceiptKind.Applied, receipt.Json);
        var errors = receipt.State!["validation"].GetProperty("errors").EnumerateArray().Select(error => error.GetRawText()).ToArray();
        Require(errors.Any(error => error.Contains("\"path\":[\"lines\",1,\"quantity\"]", StringComparison.Ordinal)
            && error.Contains(PathLine.QuantityTooSmall, StringComparison.Ordinal)), string.Join("\n", errors));
    }

    [Test]
    public async Task AReactiveAddressIsRebuiltWithItsValidationAsync()
    {
        var model = new ReactivePathInvoiceViewModel();
        using var host = new RunicWindowTestHost<ReactivePathInvoiceViewModel>(model,
            (transport, content, vm) => new ReactivePathInvoiceBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "reactivePathInvoice", ViewLocator = new TestViewLocator() });
        var initial = await host.Root.SnapshotAsync();
        Require(Errors(initial).Contains("[\"address\",\"street\"]:" + ReactivePathAddress.StreetRequired), string.Join("\n", Errors(initial)));
        var street = await host.Root.WritePathAsync(vm => vm.Address, ["street"], "Main Street 1");
        Require(street.Kind == RunicFieldWriteReceiptKind.Applied && model.Address.Street == "Main Street 1", street.Json);
        Require(!Errors(street.State!).Any(error => error.StartsWith("[\"address\",\"street\"]", StringComparison.Ordinal)),
            string.Join("\n", Errors(street.State!)));
        var line = await host.Root.WritePathAsync(vm => vm.Lines, ["1", "quantity"], 4);
        Require(line.Kind == RunicFieldWriteReceiptKind.Applied && model.Lines[0].Quantity == 4, line.Json);

        static string[] Errors(RunicViewState<ReactivePathInvoiceViewModel> state) =>
            [.. state["validation"].GetProperty("errors").EnumerateArray()
                .Select(error => error.GetProperty("path").GetRawText() + ":" + error.GetProperty("message").GetString())];
    }

    [Test]
    public void OnlyAnEditableCollectionChangesTheFingerprint()
    {
        var parts = Runic.Application.Views.Generated.BridgeContractShape.Parts(typeof(PathInvoiceViewModel));
        Require(parts.Contains($"row-editor:{typeof(PathInvoiceViewModel).FullName}:lines:UpdateLine"), string.Join("\n", parts));
        Require(!Runic.Application.Views.Generated.BridgeContractShape.Parts(typeof(FormsViewModel)).Any(part => part.StartsWith($"row-editor:{typeof(FormsViewModel).FullName}:", StringComparison.Ordinal)),
            "A collection without a row editor added a fingerprint part.");
    }

    [Test]
    public void TheGeneratedClientDescribesNestedAndRowFields()
    {
        var directory = GeneratedTypeScriptDirectory();
        var client = File.ReadAllText(Path.Combine(directory, "pathInvoice.ts")).ReplaceLineEndings("\n");
        var mock = File.ReadAllText(Path.Combine(directory, "pathInvoice.mock.ts")).ReplaceLineEndings("\n");
        foreach (var (text, expected) in new[]
        {
            (client, "  readonly lines: BridgeRowFields<{ readonly product: BridgeField<PathInvoiceClient, PathInvoiceState, string>; readonly quantity: BridgeField<PathInvoiceClient, PathInvoiceState, number>; readonly price: BridgeField<PathInvoiceClient, PathInvoiceState, string> }>;"),
            (client, "street: fieldPath<PathInvoiceClient, PathInvoiceState, string, PathAddress>({ member: \"address\", path: [\"street\"], container: state => state.address, read: (value: PathAddress) => value.street,"),
            (client, "kind: \"text\", constraints: { required: true, maxLength: 40 }, validation: state => state.validation }),"),
            (client, "lat: fieldPath<PathInvoiceClient, PathInvoiceState, number, PathAddress>({ member: \"address\", path: [\"geo\", \"lat\"], container: state => state.address, read: (value: PathAddress) => value.geo?.lat as number,"),
            (client, "quantity: fieldPath<PathInvoiceClient, PathInvoiceState, number, PathLine>({ member: \"lines\", path: [\"quantity\"], row: { key, rows: state => state.lines, keyOf: item => String(item.id) },"),
            (client, "    writePath<T>(member: string, path: readonly string[], write: BridgeFieldWriteRequest, decode: (wire: unknown) => T) { return view.write<T>(member, { ...write, path }, decode); },"),
            (mock, "  fieldPaths: { address: [[\"street\"], [\"city\"], [\"geo\"], [\"geo\", \"lat\"], [\"geo\", \"lng\"]], lines: [[\"product\"], [\"quantity\"], [\"price\"]] },"),
        })
            Require(text.Contains(expected, StringComparison.Ordinal), $"The generated client is missing:\n{expected}\n---\n{text}");
        // The key is not a field.
        Require(!client.Contains("path: [\"id\"]", StringComparison.Ordinal), client);
    }

    // Review 1: a cell write copies the row, so members the page does not see survive it.
    [Test]
    public async Task MembersThePageDoesNotSeeSurviveACellWriteAsync()
    {
        var model = new PathInvoiceViewModel();
        model.Lines[0] = model.Lines[0] with { DatabaseId = 42 };
        using var host = Toolkit(model);
        var receipt = await host.Root.WritePathAsync(vm => vm.Lines, ["1", "quantity"], 4);
        Require(receipt.Kind == RunicFieldWriteReceiptKind.Applied && model.Lines[0].Quantity == 4 && model.Lines[0].DatabaseId == 42,
            $"{receipt.Json} {model.Lines[0]}");

        // A sealed class row is copied with its copy constructor.
        var tasks = new PathTasksViewModel();
        using var taskHost = Tasks(tasks);
        var title = await taskHost.Root.WritePathAsync(vm => vm.Tasks, ["1", "title"], "Pencil");
        Require(title.Kind == RunicFieldWriteReceiptKind.Applied && tasks.Tasks[0].Title == "Pencil" && tasks.Tasks[0].Secret == "s1", title.Json);
        // [RunicNoField] leaves a member without a field.
        var status = await taskHost.Root.WritePathAsync(vm => vm.Tasks, ["1", "status"], "done");
        Require(status.Kind == RunicFieldWriteReceiptKind.Failed && tasks.Tasks[0].Status == "open", status.Json);
    }

    // Review 3: the editor receives the changed cell, the row before the edit and the connection.
    [Test]
    public async Task TheRowEditorReceivesTheEditAsync()
    {
        var model = new PathInvoiceViewModel();
        using var host = Toolkit(model);
        var before = model.Lines[1];
        await host.Root.WritePathAsync(vm => vm.Lines, ["2", "price"], "5.00");
        var edit = model.Edits.Single();
        Require(edit.Path.SequenceEqual(["price"]) && ReferenceEquals(edit.Previous, before)
            // The dispatcher's key of the connection, "{transport key}#{n}" on a transport that reuses its keys.
            && edit.ConnectionKey?.StartsWith(InMemoryBridgeTransport.DefaultConnectionKey + "#", StringComparison.Ordinal) == true, $"{string.Join(".", edit.Path)} {edit.Previous} {edit.ConnectionKey}");
    }

    // Review 3: RunicRowConflicts.Row: any change of the row after the write's baseline is a conflict.
    [Test]
    public async Task RowConflictsMakeAnyChangeOfTheRowAConflictAsync()
    {
        var tasks = new PathTasksViewModel();
        using var host = Tasks(tasks);
        var before = (await host.Root.SnapshotAsync()).Revision;
        await host.Root.WritePathAsync(vm => vm.Tasks, ["1", "title"], "Notebook");
        var owner = await host.Root.WritePathAsync(vm => vm.Tasks, ["1", "owner"], "b", expectedVersion: before);
        Require(owner.Kind == RunicFieldWriteReceiptKind.Conflict && owner.Value?.GetString() == "a" && tasks.Tasks[0].Owner == "a", owner.Json);
        var retried = await host.Root.WritePathAsync(vm => vm.Tasks, ["1", "owner"], "b", expectedVersion: owner.Version);
        Require(retried.Kind == RunicFieldWriteReceiptKind.Applied && tasks.Tasks[0].Owner == "b", retried.Json);
    }

    // Review 4: receipts of an editor that removes the row, or changes it and then fails.
    [Test]
    public async Task ReceiptsFollowWhatTheEditorDidAsync()
    {
        var model = new PathInvoiceViewModel();
        using var host = Toolkit(model);
        var exploded = await host.Root.WritePathAsync(vm => vm.Lines, ["1", "product"], "explode");
        Require(exploded.Kind == RunicFieldWriteReceiptKind.CommittedWithError && exploded.ErrorMessage == PathInvoiceViewModel.Exploded
            && exploded.Value?.GetString() == "explode" && model.Lines[0].Product == "explode", exploded.Json);
        var removed = await host.Root.WritePathAsync(vm => vm.Lines, ["2", "product"], "remove");
        Require(removed.Kind == RunicFieldWriteReceiptKind.Rejected && removed.Value is null && model.Lines.Count == 1
            && removed.ErrorMessage!.Contains("no row '2'", StringComparison.Ordinal), removed.Json);
    }

    // Review 5: a row removed and added again under its key, in separate publications and in one.
    [Test]
    public async Task ARowAddedAgainUnderItsKeyIsANewRowAsync()
    {
        var model = new PathInvoiceViewModel();
        using var host = Toolkit(model);
        var line = model.Lines[0];
        var before = (await host.Root.SnapshotAsync()).Revision;
        await InTurn(host, () => model.Lines.RemoveAt(0));
        await host.Root.SnapshotAsync();
        await InTurn(host, () => model.Lines.Insert(0, line));
        var separate = await host.Root.WritePathAsync(vm => vm.Lines, ["1", "quantity"], 3, expectedVersion: before);
        Require(separate.Kind == RunicFieldWriteReceiptKind.Conflict && model.Lines[0].Quantity == 2, separate.Json);

        before = (await host.Root.SnapshotAsync()).Revision;
        await InTurn(host, () => { model.Lines.RemoveAt(0); model.Lines.Insert(0, line); });
        var together = await host.Root.WritePathAsync(vm => vm.Lines, ["1", "quantity"], 3, expectedVersion: before);
        Require(together.Kind == RunicFieldWriteReceiptKind.Conflict && model.Lines[0].Quantity == 2, together.Json);
    }

    // Review 5: a move changes no cell.
    [Test]
    public async Task AMovedRowsCellIsWrittenFromTheStateBeforeTheMoveAsync()
    {
        var model = new PathInvoiceViewModel();
        using var host = Toolkit(model);
        var before = (await host.Root.SnapshotAsync()).Revision;
        await InTurn(host, () => model.Lines.Move(0, 1));
        var receipt = await host.Root.WritePathAsync(vm => vm.Lines, ["1", "quantity"], 6, expectedVersion: before);
        Require(receipt.Kind == RunicFieldWriteReceiptKind.Applied && model.Lines[1] is { Id: 1, Quantity: 6 }, receipt.Json);
    }

    // Review 5: the editor normalizes a neighbour cell; a draft of that cell based on the older state conflicts.
    [Test]
    public async Task ANeighbourTheEditorNormalizedConflictsAsync()
    {
        var model = new PathInvoiceViewModel();
        using var host = Toolkit(model);
        var before = (await host.Root.SnapshotAsync()).Revision;
        await host.Root.WritePathAsync(vm => vm.Lines, ["1", "quantity"], 0);
        Require(model.Lines[0].Price == 0, $"{model.Lines[0]}");
        var price = await host.Root.WritePathAsync(vm => vm.Lines, ["1", "price"], "2.00", expectedVersion: before);
        Require(price.Kind == RunicFieldWriteReceiptKind.Conflict && price.Value?.GetString() == "0", price.Json);
    }

    // Review 5: two pages write the same cell from the same state: the second is a conflict.
    [Test]
    public async Task TwoConnectionsWritingTheSameCellConflictAsync()
    {
        var model = new PathInvoiceViewModel();
        using var host = Toolkit(model);
        var revision = (await host.Root.SnapshotAsync()).Revision;
        Hello(host, "second");
        var first = Write(host, InMemoryBridgeTransport.DefaultConnectionKey, "a-1", "1", "quantity", revision, 2, 5);
        var second = Write(host, "second", "b-1", "1", "quantity", revision, 2, 7);
        Require(Receipt(first) == "applied" && Receipt(second) == "conflict" && model.Lines[0].Quantity == 5, $"{first}\n{second}");
    }

    // Review 5: a retry whose receipt was evicted is a conflict, never applied again.
    [Test]
    public async Task ARetryAfterItsReceiptWasEvictedIsNotAppliedAgainAsync()
    {
        var model = new PathInvoiceViewModel();
        using var host = Toolkit(model);
        var revision = (await host.Root.SnapshotAsync()).Revision;
        var first = Write(host, InMemoryBridgeTransport.DefaultConnectionKey, "w-first", "2", "product", revision, "Ink", "Toner");
        Require(Receipt(first) == "applied", first);
        for (var index = 0; index < 70; index++)
            await host.Root.WritePathAsync(vm => vm.Lines, ["1", "product"], $"Pens {index}");
        await host.Root.WritePathAsync(vm => vm.Lines, ["2", "product"], "Ink");
        var retry = Write(host, InMemoryBridgeTransport.DefaultConnectionKey, "w-first", "2", "product", revision, "Ink", "Toner");
        Require(Receipt(retry) == "conflict" && model.Lines[1].Product == "Ink", retry);
    }

    // Review 2: one cell change in a 10,000-row grid encodes a few rows, not the grid.
    [Test]
    public async Task ACellWriteInALargeGridEncodesOnlyTheChangedRowAsync()
    {
        var model = new PathRowsViewModel();
        for (var id = 2; id <= 10_000; id++) model.Lines.Add(new(id, $"Item {id}", 1, 1m));
        var encodes = 0;
        using var host = new RunicWindowTestHost<PathRowsViewModel>(model,
            (transport, content, vm) => new CountingRowsBridge(transport, vm, content, () => encodes++),
            new RunicWindowTestHostOptions { RootRoute = "countingRows", ViewLocator = new TestViewLocator() });
        var revision = (await host.Root.SnapshotAsync()).Revision;
        encodes = 0;
        var reply = Write(host, InMemoryBridgeTransport.DefaultConnectionKey, "w-big", "5000", "quantity", revision, 1, 9);
        Require(Receipt(reply) == "applied" && model.Lines[4999].Quantity == 9, reply);
        Require(encodes <= 5, $"One cell write encoded {encodes} rows.");
    }

    private static RunicWindowTestHost<PathTasksViewModel> Tasks(PathTasksViewModel model) =>
        new(model, (transport, content, vm) => new PathTasksBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "pathTasks", ViewLocator = new TestViewLocator() });

    // Changes made in one model turn are one publication unit.
    private static Task InTurn<T>(RunicWindowTestHost<T> host, Action change) where T : class
    {
        Runic.Application.Views.RunicModelTurns.Run(host.Content.ModelContext!, change);
        return Task.CompletedTask;
    }

    private static void Hello<T>(RunicWindowTestHost<T> host, string connection) where T : class =>
        host.Transport.Request($$"""{"kind":"hello","protocol":4,"session":"{{connection}}-session","client":{"name":"probe","version":"1"},"features":["field-paths/1"]}""", connection);

    private static string Write<T>(RunicWindowTestHost<T> host, string connection, string requestId, string key, string cell, long version,
        object expected, object value) where T : class =>
        host.Transport.Request(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["kind"] = "write", ["route"] = host.RootRoute, ["member"] = "lines", ["path"] = new[] { key, cell }, ["requestId"] = requestId,
            ["expectedVersion"] = version, ["expectedValue"] = expected, ["value"] = value,
        }), connection);

    private static string? Receipt(string reply)
    {
        using var document = JsonDocument.Parse(reply);
        return document.RootElement.TryGetProperty("receipt", out var receipt) ? receipt.GetProperty("kind").GetString() : null;
    }

    // A hand-written Bridge of PathRowsViewModel that counts how often it encodes a row.
    private sealed class CountingRowsBridge(IBridgeTransport transport, PathRowsViewModel vm, WindowContentSession content, Action encoded)
        : Runic.Application.Views.Generated.ViewModelBridge(transport, vm, typeof(PathRowsViewModel), "countingRows",
            [new("lines", (writer, model) => { writer.WriteStartArray(); foreach (var line in ((PathRowsViewModel)model).Lines) WriteLine(writer, line); writer.WriteEndArray(); })],
            [new("Lines", "lines", model => ((PathRowsViewModel)model).Lines, null)], [], [], "counting-rows", content,
            collections:
            [
                new("lines", model => ((PathRowsViewModel)model).Lines, (writer, row) => { encoded(); WriteLine(writer, (PathLine)row!); },
                    row => ((PathLine)row!).Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    FieldPaths = [new(["quantity"], (row, value) => (PathLine)row with { Quantity = value.GetInt32() })],
                    UpdateRow = (model, row, _) => ((PathRowsViewModel)model).UpdateLine(((PathLine)row!).Id, (PathLine)row),
                },
            ])
    {
        private static void WriteLine(Utf8JsonWriter writer, PathLine line)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", line.Id);
            writer.WriteString("product", line.Product);
            writer.WriteNumber("quantity", line.Quantity);
            writer.WriteString("price", line.Price.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
    }

    private static string GeneratedTypeScriptDirectory()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx")))
                return Path.Combine(directory.FullName, "tests", "dotnet", "Runic.Application.Testing.Tests", "obj", "bridge-frontend", "generated");
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
