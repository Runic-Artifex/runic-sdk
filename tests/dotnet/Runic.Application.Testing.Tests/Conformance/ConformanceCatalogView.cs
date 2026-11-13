using System.Text.Json.Nodes;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests.Conformance;

// The catalog View (specs/application/conformance/README.md, "Test Views"): a query collection `rows` of {id, label}
// rows keyed by id, over an in-memory row source with the fields id and label and the custom filter even. Its first
// count is taken before the window opens, so the first state carries the total.
internal sealed class CatalogConformanceViewModel : IConformanceModel, System.ComponentModel.INotifyPropertyChanged
{
    private ConformanceRow[] _rows;

    private CatalogConformanceViewModel(ConformanceRow[] rows, IRunicModelContext? context)
    {
        _rows = rows;
        var fields = new RunicQueryFields<ConformanceRow>()
            .Add("id", row => row.Id)
            .AddText("label", row => row.Label)
            .AddCustom("even", (source, _) => source.Where(row => row.Id % 2 == 0));
        Rows = new(new RunicQueryableRowSource<ConformanceRow, int>(() => Volatile.Read(ref _rows).AsQueryable(), fields, row => row.Id),
            row => row.Id, new RunicQueryCollectionOptions { ModelContext = context });
        Rows.LoadAsync(new RunicRowRange(0, 0)).AsTask().GetAwaiter().GetResult();
        // The count is published in a turn of the model context; the window opens after it.
        for (var waited = 0; Rows.Total is null; waited++)
        {
            if (waited > 5_000) throw new TimeoutException("The catalog's first count was not published.");
            Thread.Sleep(1);
        }
    }

    public RunicQueryCollection<ConformanceRow, int> Rows { get; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    internal static CatalogConformanceViewModel Create(JsonObject model, IRunicModelContext? context) =>
        new(model["rows"] is JsonArray rows ? [.. ConformanceRow.Expand(rows)] : [], context);

    // reset replaces the rows, as after rows were added or removed (Refresh); replaceAt changes one in place (Invalidate).
    public void Apply(JsonObject mutation, Func<IDisposable>? unit)
    {
        switch (mutation["op"]!.GetValue<string>())
        {
            case "reset":
                Volatile.Write(ref _rows, [.. ConformanceRow.Expand(mutation["items"]!.AsArray())]);
                Rows.Refresh();
                break;
            case "replaceAt":
                var index = mutation["index"]!.GetValue<int>();
                var row = ConformanceRow.Read(mutation["item"]);
                var rows = (ConformanceRow[])Volatile.Read(ref _rows).Clone();
                rows[index] = row;
                Volatile.Write(ref _rows, rows);
                Rows.Invalidate(row.Id);
                break;
            default:
                throw new NotSupportedException($"The catalog View has no mutation {mutation.ToJsonString()}.");
        }
    }

    public JsonNode? Read(string member) => member switch
    {
        "rows" => JsonNode.Parse(Rows.Query.ToString()),
        _ => throw new NotSupportedException($"The catalog View has no member {member}."),
    };

    public JsonNode? Provide(string id) => null;
}

internal sealed class CatalogConformanceBridge(IBridgeTransport transport, CatalogConformanceViewModel vm, WindowContentSession content)
    : ViewModelBridge(transport, vm, typeof(CatalogConformanceViewModel), "catalog",
        [new("rows", static (writer, model) => BridgeQueryWire.WriteState(writer, ((CatalogConformanceViewModel)model).Rows))],
        [
            new("Rows", "rows", model => ((CatalogConformanceViewModel)model).Rows, null, static model => ((CatalogConformanceViewModel)model).Rows)
                { QueryRowWriter = static (writer, row) => ConformanceRow.Write(writer, (ConformanceRow)row!) },
        ],
        [], [], "conformance-catalog", content);
