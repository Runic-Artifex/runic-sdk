using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using ReactiveUI;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

public class DataShapeBase : ReactiveObject
{
    public string Source { get; set; } = "base";
}

public sealed class DataShapeItem : DataShapeBase
{
    private string _name = string.Empty;
    private int? _retryAfter;
    [RunicAlias("__proto__")]
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
    [RunicAlias("retry-after")]
    public int? RetryAfter { get => _retryAfter; set => this.RaiseAndSetIfChanged(ref _retryAfter, value); }
}

public sealed class DataShapeGroup : ReactiveObject
{
    public ObservableCollection<DataShapeItem> Items { get; set; } =
    [new DataShapeItem { Name = "nested-first", RetryAfter = 5 }];
}

[RunicUnion(typeof(DataShapeText), typeof(DataShapeCount))]
public abstract record DataShapePayload;

[RunicUnionCase("text")]
public sealed record DataShapeText(string Text) : DataShapePayload;

[RunicUnionCase("count")]
public sealed record DataShapeCount(int Count) : DataShapePayload;

[RunicBridgeCodec(typeof(DataShapeMoneyCodec))]
public readonly record struct DataShapeMoney(decimal Value);

[RunicCodecShape("string", "bridgeWire.decimal($value)")]
public sealed class DataShapeMoneyCodec : IRunicBridgeCodec<DataShapeMoney>
{
    public static DataShapeMoney Read(JsonElement value) => new(BridgeWire.ReadDecimal(value));
    public static void Write(Utf8JsonWriter writer, DataShapeMoney value) => BridgeWire.WriteDecimal(writer, value.Value);
}

public sealed class DataShapeViewModel : ReactiveObject
{
    private int? _optional;
    private long _exactId = 9_007_199_254_740_993;
    [RunicAlias("exact-id")]
    public long ExactId
    {
        get => _exactId;
        set { _exactId = value; if (value == -1) throw new InvalidOperationException("Fixture post-apply failure."); }
    }
    public decimal Amount { get; set; } = 1234567890.123456789m;
    public DateOnly Day { get; set; } = new(2026, 9, 28);
    public DateTime When { get; set; } = new(2026, 9, 28, 10, 11, 12, DateTimeKind.Unspecified);
    public TimeSpan Duration { get; set; } = TimeSpan.FromTicks(123456789);
    public DataShapeItem? OptionalItem { get; set; }
    public int? Optional { get => _optional; set => this.RaiseAndSetIfChanged(ref _optional, value); }
    public ObservableCollection<DataShapeItem> Items { get; } = [new DataShapeItem { Name = "first", RetryAfter = 3 }];
    public List<DataShapeItem?> OptionalItems { get; } = [null, new DataShapeItem { Name = "optional-item" }];
    public ObservableCollection<DataShapeGroup> Groups { get; } = [new DataShapeGroup()];
    public Dictionary<string, DataShapeItem> Lookup { get; } = new(StringComparer.Ordinal)
    {
        ["first"] = new DataShapeItem { Name = "lookup" },
        // This is a legal .NET dictionary key and must stay an own property in
        // generated JavaScript records instead of mutating their prototype.
        ["__proto__"] = new DataShapeItem { Name = "prototype-safe" },
    };
    public DataShapePayload Payload { get; set; } = new DataShapeText("payload");
    public DataShapeMoney Money { get; set; } = new(12.50m);
    public DataShapeItem Whole { get; set; } = new() { Name = "whole" };
}

public sealed partial class DataShapeWindow(DataShapeViewModel model) : RunicWindow<DataShapeViewModel>(model);
