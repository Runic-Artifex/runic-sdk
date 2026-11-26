using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Views;
using Runic.Application.Views.Generated;

namespace Runic.Application.Testing.Tests.Conformance;

// The invoice View (specs/application/conformance/README.md, "Test Views"): an address object whose members are
// fields (field-paths/1), and lines whose cells are fields through a row editor. Rows and the address are immutable
// records, which path writes replace.
internal sealed record InvoiceAddress(string Street, string City)
{
    internal static InvoiceAddress Read(JsonElement value) => new(ConformanceWire.String(value.GetProperty("street")),
        ConformanceWire.String(value.GetProperty("city")));

    internal static InvoiceAddress Read(JsonNode? node) => new(node!["street"]!.GetValue<string>(), node["city"]!.GetValue<string>());

    internal static void Write(Utf8JsonWriter writer, InvoiceAddress address)
    {
        writer.WriteStartObject();
        writer.WriteString("street", address.Street);
        writer.WriteString("city", address.City);
        writer.WriteEndObject();
    }

    internal JsonNode ToNode() => new JsonObject { ["street"] = Street, ["city"] = City };
}

// The nullable billing member: a name and a nested address, whose fields are field paths two deep.
internal sealed record InvoiceBilling(string Name, InvoiceAddress Address)
{
    internal static InvoiceBilling? Read(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null
        : new(ConformanceWire.String(value.GetProperty("name")), InvoiceAddress.Read(value.GetProperty("address")));

    internal static InvoiceBilling? Read(JsonNode? node) => node is null ? null
        : new(node["name"]!.GetValue<string>(), InvoiceAddress.Read(node["address"]));

    internal static void Write(Utf8JsonWriter writer, InvoiceBilling? billing)
    {
        if (billing is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("name", billing.Name);
        writer.WritePropertyName("address");
        InvoiceAddress.Write(writer, billing.Address);
        writer.WriteEndObject();
    }

    internal JsonNode ToNode() => new JsonObject { ["name"] = Name, ["address"] = Address.ToNode() };
}

internal sealed record InvoiceLine(int Id, string Label, int Quantity)
{
    internal static int ReadQuantity(JsonElement quantity) =>
        quantity.ValueKind == JsonValueKind.Number && quantity.TryGetInt32(out var count) ? count : throw new FormatException("Expected an int32.");

    internal static InvoiceLine Read(JsonNode? node) =>
        new(node!["id"]!.GetValue<int>(), node["label"]!.GetValue<string>(), node["quantity"]!.GetValue<int>());

    internal static void Write(Utf8JsonWriter writer, InvoiceLine line)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", line.Id);
        writer.WriteString("label", line.Label);
        writer.WriteNumber("quantity", line.Quantity);
        writer.WriteEndObject();
    }

    internal static string KeyOf(object? row) => ((InvoiceLine)row!).Id.ToString(CultureInfo.InvariantCulture);

    internal JsonNode ToNode() => new JsonObject { ["id"] = Id, ["label"] = Label, ["quantity"] = Quantity };
}

internal sealed class InvoiceViewModel : ConformanceModel
{
    internal const string NegativeQuantity = "Quantity must not be negative.";

    public InvoiceAddress Address
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Raise();
        }
    } = new("", "");

    public InvoiceBilling? Billing
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Raise();
        }
    }

    public ObservableCollection<InvoiceLine> Lines { get; } = [];

    // The arrange value rowConflicts (not a state member): the row editor checks a line as a whole.
    internal bool RowConflicts { get; private init; }

    // The row editor: replaces the line with its key, and refuses a negative quantity.
    internal void UpdateLine(int id, InvoiceLine line)
    {
        if (line.Quantity < 0) throw new ArgumentException(NegativeQuantity);
        for (var index = 0; index < Lines.Count; index++)
        {
            if (Lines[index].Id != id) continue;
            Lines[index] = line;
            return;
        }
        throw new InvalidOperationException($"No line {id}.");
    }

    internal static InvoiceViewModel Create(JsonObject model)
    {
        var vm = new InvoiceViewModel { RowConflicts = model["rowConflicts"]?.GetValue<bool>() == true };
        if (model["address"] is { } address) vm.Address = InvoiceAddress.Read(address);
        if (model["billing"] is { } billing) vm.Billing = InvoiceBilling.Read(billing);
        if (model["lines"] is JsonArray lines) foreach (var line in lines) vm.Lines.Add(InvoiceLine.Read(line));
        return vm;
    }

    internal override void Apply(JsonObject mutation, Func<IDisposable>? unit)
    {
        int Integer(string name) => mutation[name]!.GetValue<int>();
        switch (mutation["op"]!.GetValue<string>(), mutation["member"]?.GetValue<string>())
        {
            case ("assign", "address"): Address = InvoiceAddress.Read(mutation["value"]); break;
            case ("assign", "billing"): Billing = InvoiceBilling.Read(mutation["value"]); break;
            case ("insert", "lines"): Lines.Insert(Integer("index"), InvoiceLine.Read(mutation["item"])); break;
            case ("replaceAt", "lines"): Lines[Integer("index")] = InvoiceLine.Read(mutation["item"]); break;
            case ("removeAt", "lines"): Lines.RemoveAt(Integer("index")); break;
            default: throw new NotSupportedException($"The invoice View has no mutation {mutation.ToJsonString()}.");
        }
    }

    internal override JsonNode? Read(string member) => member switch
    {
        "address" => Address.ToNode(),
        "billing" => Billing?.ToNode(),
        "lines" => new JsonArray([.. Lines.Select(line => line.ToNode())]),
        _ => throw new NotSupportedException($"The invoice View has no member {member}."),
    };
}

internal sealed class InvoiceBridge(IBridgeTransport transport, InvoiceViewModel vm, WindowContentSession content)
    : ViewModelBridge(transport, vm, typeof(InvoiceViewModel), "invoice",
        [
            new("address", (writer, model) => InvoiceAddress.Write(writer, ((InvoiceViewModel)model).Address)),
            new("billing", (writer, model) => InvoiceBilling.Write(writer, ((InvoiceViewModel)model).Billing)),
            new("lines", (writer, model) => { writer.WriteStartArray(); foreach (var line in ((InvoiceViewModel)model).Lines) InvoiceLine.Write(writer, line); writer.WriteEndArray(); }),
        ],
        [
            new("Address", "address", model => ((InvoiceViewModel)model).Address, (model, value) => ((InvoiceViewModel)model).Address = InvoiceAddress.Read(value)),
            new("Billing", "billing", model => ((InvoiceViewModel)model).Billing, (model, value) => ((InvoiceViewModel)model).Billing = InvoiceBilling.Read(value)),
            new("Lines", "lines", model => ((InvoiceViewModel)model).Lines, null),
        ],
        [
            new("Address", "address", CheckedFieldValueKind.Json, model => ((InvoiceViewModel)model).Address, (model, value) => ((InvoiceViewModel)model).Address = (InvoiceAddress)value!,
                readValue: InvoiceAddress.Read, writeValue: (writer, value) => InvoiceAddress.Write(writer, (InvoiceAddress)value!))
            {
                FieldPaths =
                [
                    new(["street"], (address, value) => (InvoiceAddress)address with { Street = ConformanceWire.String(value) }),
                    new(["city"], (address, value) => (InvoiceAddress)address with { City = ConformanceWire.String(value) }),
                ],
            },
            new("Billing", "billing", CheckedFieldValueKind.Json, model => ((InvoiceViewModel)model).Billing, (model, value) => ((InvoiceViewModel)model).Billing = (InvoiceBilling?)value,
                readValue: element => InvoiceBilling.Read(element), writeValue: (writer, value) => InvoiceBilling.Write(writer, (InvoiceBilling?)value))
            {
                FieldPaths =
                [
                    new(["name"], (billing, value) => (InvoiceBilling)billing with { Name = ConformanceWire.String(value) }),
                    new(["address"], (billing, value) => (InvoiceBilling)billing with { Address = InvoiceAddress.Read(value) }),
                    new(["address", "street"], (billing, value) =>
                        (InvoiceBilling)billing with { Address = ((InvoiceBilling)billing).Address with { Street = ConformanceWire.String(value) } }),
                    new(["address", "city"], (billing, value) =>
                        (InvoiceBilling)billing with { Address = ((InvoiceBilling)billing).Address with { City = ConformanceWire.String(value) } }),
                ],
            },
        ],
        [], ConformanceContracts.Fingerprint(typeof(InvoiceViewModel)), content,
        collections:
        [
            new("lines", model => ((InvoiceViewModel)model).Lines, (writer, row) => InvoiceLine.Write(writer, (InvoiceLine)row!), InvoiceLine.KeyOf)
            {
                FieldPaths =
                [
                    new(["label"], (line, value) => (InvoiceLine)line with { Label = ConformanceWire.String(value) }),
                    new(["quantity"], (line, value) => (InvoiceLine)line with { Quantity = InvoiceLine.ReadQuantity(value) }),
                ],
                UpdateRow = (model, row, _) => ((InvoiceViewModel)model).UpdateLine(((InvoiceLine)row!).Id, (InvoiceLine)row),
                RowConflicts = vm.RowConflicts ? RunicRowConflicts.Row : RunicRowConflicts.Cell,
            },
        ]);
