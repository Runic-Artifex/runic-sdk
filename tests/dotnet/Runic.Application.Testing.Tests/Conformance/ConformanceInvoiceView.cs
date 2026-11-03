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

internal sealed record InvoiceLine(int Id, string Label, int Quantity)
{
    internal static InvoiceLine Read(JsonElement value)
    {
        var quantity = value.GetProperty("quantity");
        if (quantity.ValueKind != JsonValueKind.Number || !quantity.TryGetInt32(out var count)) throw new FormatException("Expected an int32.");
        return new(value.GetProperty("id").GetInt32(), ConformanceWire.String(value.GetProperty("label")), count);
    }

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

    public ObservableCollection<InvoiceLine> Lines { get; } = [];

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
        var vm = new InvoiceViewModel();
        if (model["address"] is { } address) vm.Address = InvoiceAddress.Read(address);
        if (model["lines"] is JsonArray lines) foreach (var line in lines) vm.Lines.Add(InvoiceLine.Read(line));
        return vm;
    }

    internal override void Apply(JsonObject mutation, Func<IDisposable>? unit)
    {
        int Integer(string name) => mutation[name]!.GetValue<int>();
        switch (mutation["op"]!.GetValue<string>(), mutation["member"]?.GetValue<string>())
        {
            case ("assign", "address"): Address = InvoiceAddress.Read(mutation["value"]); break;
            case ("insert", "lines"): Lines.Insert(Integer("index"), InvoiceLine.Read(mutation["item"])); break;
            case ("replaceAt", "lines"): Lines[Integer("index")] = InvoiceLine.Read(mutation["item"]); break;
            case ("removeAt", "lines"): Lines.RemoveAt(Integer("index")); break;
            default: throw new NotSupportedException($"The invoice View has no mutation {mutation.ToJsonString()}.");
        }
    }

    internal override JsonNode? Read(string member) => member switch
    {
        "address" => Address.ToNode(),
        "lines" => new JsonArray([.. Lines.Select(line => line.ToNode())]),
        _ => throw new NotSupportedException($"The invoice View has no member {member}."),
    };
}

internal sealed class InvoiceBridge(IBridgeTransport transport, InvoiceViewModel vm, WindowContentSession content)
    : ViewModelBridge<InvoiceViewModel>(transport, vm, "invoice",
        [
            new("address", (writer, model) => InvoiceAddress.Write(writer, model.Address)),
            new("lines", (writer, model) => { writer.WriteStartArray(); foreach (var line in model.Lines) InvoiceLine.Write(writer, line); writer.WriteEndArray(); }),
        ],
        [
            new("Address", "address", model => model.Address, (model, value) => model.Address = InvoiceAddress.Read(value)),
            new("Lines", "lines", model => model.Lines, null),
        ],
        [
            new("Address", "address", CheckedFieldValueKind.Json, model => model.Address, (model, value) => model.Address = (InvoiceAddress)value!,
                readValue: InvoiceAddress.Read, writeValue: (writer, value) => InvoiceAddress.Write(writer, (InvoiceAddress)value!))
            { FieldPaths = [["street"], ["city"]] },
        ],
        [], "conformance-invoice", content,
        collections:
        [
            new("lines", model => model.Lines, (writer, row) => InvoiceLine.Write(writer, (InvoiceLine)row!), InvoiceLine.KeyOf)
            {
                FieldPaths = [["label"], ["quantity"]],
                ReadRow = element => InvoiceLine.Read(element),
                UpdateRow = (model, row) => model.UpdateLine(((InvoiceLine)row!).Id, (InvoiceLine)row),
            },
        ]);
