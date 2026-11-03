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
