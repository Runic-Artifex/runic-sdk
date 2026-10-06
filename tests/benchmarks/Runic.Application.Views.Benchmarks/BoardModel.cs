using System.ComponentModel;
using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Views.Benchmarks;

// A representative model: a scalar that changes in bursts, a list of record
// rows and one structured checked field. The hand-written writers follow the
// generated ones: members in declaration order, so not in canonical order.
public sealed record Row(int Id, string Title, string Notes, bool Done, double Score, string[] Tags);

public sealed record Preferences(string Theme, int FontSize, bool Compact, double Zoom, string[] Pinned,
    IReadOnlyDictionary<string, string> Shortcuts);

public sealed class BoardModel : INotifyPropertyChanged
{
    private int _counter;
    private Preferences _preferences = Wire.SamplePreferences(0);

    public BoardModel(int rows) => Rows = [.. Enumerable.Range(0, rows).Select(Wire.SampleRow)];

    public Row[] Rows { get; }

    public int Counter
    {
        get => _counter;
        set { _counter = value; PropertyChanged?.Invoke(this, new(nameof(Counter))); }
    }

    public Preferences Preferences
    {
        get => _preferences;
        set { _preferences = value; PropertyChanged?.Invoke(this, new(nameof(Preferences))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class Wire
{
    public static Row SampleRow(int id) => new(id, $"Task {id}", $"Notes for task {id} with some ordinary text.",
        id % 3 == 0, id * 1.25, [$"tag{id % 5}", "board"]);

    public static Preferences SamplePreferences(int variant) => new(variant % 2 == 0 ? "dark" : "light", 14 + variant % 2, variant % 2 == 1,
        1.5, ["inbox", "today", "later"], new Dictionary<string, string>
        {
            ["save"] = "Ctrl+S", ["open"] = "Ctrl+O", ["find"] = "Ctrl+F", ["close"] = "Ctrl+W",
        });

    public static void WriteRow(Utf8JsonWriter writer, Row row)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", row.Id);
        writer.WriteString("title", row.Title);
        writer.WriteString("notes", row.Notes);
        writer.WriteBoolean("done", row.Done);
        writer.WriteNumber("score", row.Score);
        writer.WriteStartArray("tags");
        foreach (var tag in row.Tags) writer.WriteStringValue(tag);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static void WritePreferences(Utf8JsonWriter writer, Preferences value)
    {
        writer.WriteStartObject();
        writer.WriteString("theme", value.Theme);
        writer.WriteNumber("fontSize", value.FontSize);
        writer.WriteBoolean("compact", value.Compact);
        writer.WriteNumber("zoom", value.Zoom);
        writer.WriteStartArray("pinned");
        foreach (var item in value.Pinned) writer.WriteStringValue(item);
        writer.WriteEndArray();
        writer.WriteStartObject("shortcuts");
        foreach (var (key, shortcut) in value.Shortcuts) writer.WriteString(key, shortcut);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    public static Preferences ReadPreferences(JsonElement element) => new(
        element.GetProperty("theme").GetString()!,
        element.GetProperty("fontSize").GetInt32(),
        element.GetProperty("compact").GetBoolean(),
        element.GetProperty("zoom").GetDouble(),
        [.. element.GetProperty("pinned").EnumerateArray().Select(item => item.GetString()!)],
        element.GetProperty("shortcuts").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!));

    public static void WriteSnapshot(Utf8JsonWriter writer, BoardModel model, long revision, Action<Utf8JsonWriter> writeFields)
    {
        writer.WriteStartObject();
        writer.WriteNumber("revision", revision);
        writer.WriteNumber("counter", model.Counter);
        writer.WritePropertyName("preferences");
        WritePreferences(writer, model.Preferences);
        writer.WriteStartArray("rows");
        foreach (var row in model.Rows) WriteRow(writer, row);
        writer.WriteEndArray();
        writeFields(writer);
        writer.WriteEndObject();
    }
}

public sealed class BoardBridge(IBridgeTransport transport, BoardModel model, WindowContentSession? content)
    : ViewModelBridge<BoardModel>(transport, model, "board", Wire.WriteSnapshot, [],
        content is null ? [] :
        [
            new("Preferences", "preferences", CheckedFieldValueKind.Json, vm => vm.Preferences,
                (vm, value) => vm.Preferences = (Preferences)value!,
                ReadValue: element => Wire.ReadPreferences(element),
                WriteValue: (writer, value) => Wire.WritePreferences(writer, (Preferences)value!)),
        ],
        [], contractFingerprint: "benchmarks", content: content);
