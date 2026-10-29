using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using System.Xml;
using Runic.Application.Views;
using Runic.Application.Views.Generated;

namespace Runic.Application.Testing.Tests.Conformance;

// The test Views of the conformance corpus (specs/application/conformance/README.md, "Test Views" and "Mutations"),
// implemented by the producer under test. The scenario runner owns the transport, the virtual clock and the wire; this
// hook owns the window and its model:
//
// - CreateWindow creates the window of one scenario: a WindowContentSession on settings.Transport with
//   settings.TimeProvider and settings.MaximumInputBytes, failure detail off (IncludeFailureDetail = false), whose root
//   View is `view` ("list", "log", "notes" or "form"), serving the route of that name, with the values of arrange.model
//   (generators expanded; members it does not list keep their defaults). The notes View also serves the detail View in
//   its detail slot with the content id 0123456789abcdef0123456789abcdef.
// - IConformanceWindow.MutateAsync applies the mutations of one mutate step to the root View's model, as one
//   publication unit, or each mutation and each appended row as its own unit when eachUnit is true.
// - IConformanceWindow.ReadMember returns a member of the root View's model as its wire JSON value, for the runner to
//   compare with expect.model.
// - IConformanceWindow.Provide returns the value of a provided id (README "Identifiers") that the runner writes before
//   any message carried it, or null when the window does not know it. The corpus names the operation contract of the
//   notes View's slowImport "contract" and the contract of its confirm interaction "confirmContract".
internal interface IConformanceViews
{
    IConformanceWindow CreateWindow(string view, JsonObject model, ConformanceWindowSettings settings);
}

internal interface IConformanceWindow : IDisposable
{
    Task MutateAsync(JsonArray mutations, bool eachUnit);

    JsonNode? ReadMember(string member);

    JsonNode? Provide(string id);
}

// What the runner gives the window: its transport (the ScenarioTransport), its virtual clock and its input limit
// (the scenario's options.inputBytes, or null for the default).
internal sealed record ConformanceWindowSettings(IBridgeTransport Transport, TimeProvider TimeProvider, int? MaximumInputBytes);

internal static class ConformanceViews
{
    // The implementation the producer scenarios run against.
    internal static IConformanceViews Producer { get; } = new DotNetConformanceViews();
}

// The .NET producer's test Views. The Bridges are written by hand against the generated-code API of protocol 4
// (BridgeStateMember, JsonElement setters, ReadInput), so the scenarios fix the members each View has exactly.
internal sealed class DotNetConformanceViews : IConformanceViews
{
    internal const string DetailId = "0123456789abcdef0123456789abcdef";

    public IConformanceWindow CreateWindow(string view, JsonObject model, ConformanceWindowSettings settings)
    {
        var ids = 0;
        var session = new WindowContentSession(settings.Transport, new WindowContentSessionOptions
        {
            TimeProvider = settings.TimeProvider,
            MaximumInputBytes = settings.MaximumInputBytes,
            IncludeFailureDetail = false,
            // The detail View's content id is fixed; later ids (interaction requests) are counted.
            CreateId = () => Interlocked.Increment(ref ids) == 1 ? DetailId : $"id{ids}",
        });
        try
        {
            return view switch
            {
                "list" => new Window<ListViewModel>(session, ListViewModel.Create(model),
                    (vm, content) => new ListBridge(settings.Transport, vm, content)),
                "log" => new Window<LogViewModel>(session, LogViewModel.Create(model),
                    (vm, content) => new LogBridge(settings.Transport, vm, content)),
                "notes" => new Window<NotesViewModel>(session, NotesViewModel.Create(model),
                    (vm, content) => new NotesBridge(settings.Transport, vm, content)),
                "form" => new Window<FormViewModel>(session, FormViewModel.Create(model),
                    (vm, content) => new FormBridge(settings.Transport, vm, content)),
                _ => throw new NotSupportedException($"The test View '{view}' is not a root View."),
            };
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private sealed class Window<TModel> : IConformanceWindow where TModel : class, IConformanceModel
    {
        private readonly WindowContentSession _session;
        private readonly TModel _model;
        private readonly IDisposable _bridge;

        public Window(WindowContentSession session, TModel model, Func<TModel, WindowContentSession, IDisposable> attach)
        {
            _session = session;
            _model = model;
            _bridge = RunicModelTurns.Run(session.ModelContext!, () => attach(model, session));
        }

        public Task MutateAsync(JsonArray mutations, bool eachUnit)
        {
            RunicModelTurns.Run(_session.ModelContext!, () =>
            {
                if (!eachUnit)
                {
                    using (BridgeSnapshotBatch.Begin(_model))
                        foreach (var mutation in mutations) _model.Apply(mutation!.AsObject(), unit: null);
                    return;
                }
                foreach (var mutation in mutations)
                {
                    // appendRows makes each appended row its own unit; every other mutation is one unit.
                    if (mutation!["op"]!.GetValue<string>() == "appendRows")
                        _model.Apply(mutation.AsObject(), unit: () => BridgeSnapshotBatch.Begin(_model));
                    else
                        using (BridgeSnapshotBatch.Begin(_model)) _model.Apply(mutation.AsObject(), unit: null);
                }
            });
            return Task.CompletedTask;
        }

        public JsonNode? ReadMember(string member) => RunicModelTurns.Run(_session.ModelContext!, () => _model.Read(member));

        public JsonNode? Provide(string id) => _model.Provide(id);

        public void Dispose()
        {
            try { _bridge.Dispose(); }
            finally { _session.Dispose(); }
        }
    }
}

// A test View's model: the mutations of the corpus and its members' wire values.
internal interface IConformanceModel
{
    void Apply(JsonObject mutation, Func<IDisposable>? unit);

    JsonNode? Read(string member);

    JsonNode? Provide(string id);
}

// The base of the test Views' models: change notification and the mutations of the corpus.
internal abstract class ConformanceModel : INotifyPropertyChanged, IConformanceModel
{
    void IConformanceModel.Apply(JsonObject mutation, Func<IDisposable>? unit) => Apply(mutation, unit);

    JsonNode? IConformanceModel.Read(string member) => Read(member);

    JsonNode? IConformanceModel.Provide(string id) => Provide(id);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    // Applies one mutation. unit opens a publication unit for each row that appendRows appends; null when the caller
    // already opened the unit.
    internal abstract void Apply(JsonObject mutation, Func<IDisposable>? unit);

    internal abstract JsonNode? Read(string member);

    internal virtual JsonNode? Provide(string id) => null;

}

// A row of the list and notes Views: {id: int32, label: string}, keyed by id.
internal sealed record ConformanceRow(int Id, string Label)
{
    internal static ConformanceRow Read(JsonNode? node) =>
        new(node!["id"]!.GetValue<int>(), node["label"]!.GetValue<string>());

    internal static void Write(Utf8JsonWriter writer, ConformanceRow row)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", row.Id);
        writer.WriteString("label", row.Label);
        writer.WriteEndObject();
    }

    internal static JsonNode ToNode(ConformanceRow row) => new JsonObject { ["id"] = row.Id, ["label"] = row.Label };

    internal static string KeyOf(object? row) => ((ConformanceRow)row!).Id.ToString(CultureInfo.InvariantCulture);

    // The rows of the $rows generator: ids from start, label "row {id}" padded with '.' to width.
    internal static IEnumerable<ConformanceRow> Generate(int start, int count, int width) =>
        Enumerable.Range(start, count).Select(id =>
        {
            var label = $"row {id}";
            return new ConformanceRow(id, label.Length < width ? label.PadRight(width, '.') : label);
        });

    // Expands a list of rows that may contain $rows generators.
    internal static IEnumerable<ConformanceRow> Expand(JsonArray rows)
    {
        foreach (var row in rows)
        {
            if (row is JsonObject { Count: 1 } generator && generator["$rows"] is JsonArray parameters)
            {
                foreach (var generated in Generate(parameters[0]!.GetValue<int>(), parameters[1]!.GetValue<int>(),
                    parameters.Count > 2 ? parameters[2]!.GetValue<int>() : 0)) yield return generated;
            }
            else yield return Read(row);
        }
    }
}

// The rows of a test View: an observable collection that can also be reset at once.
internal sealed class ConformanceRows : ObservableCollection<ConformanceRow>
{
    internal void ResetTo(IEnumerable<ConformanceRow> rows)
    {
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    // Appends rows as one change, as slowImport's finish does.
    internal void AddRange(IReadOnlyList<ConformanceRow> rows)
    {
        if (rows.Count == 0) return;
        var index = Count;
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, rows.ToList(), index));
    }

    // The row mutations: insert, removeAt, replaceAt, move, reset and appendRows.
    internal bool Apply(JsonObject mutation, Func<IDisposable>? unit)
    {
        int Integer(string name) => mutation[name]!.GetValue<int>();
        switch (mutation["op"]!.GetValue<string>())
        {
            case "insert": Insert(Integer("index"), ConformanceRow.Read(mutation["item"])); return true;
            case "removeAt": RemoveAt(Integer("index")); return true;
            case "replaceAt": this[Integer("index")] = ConformanceRow.Read(mutation["item"]); return true;
            case "move": Move(Integer("oldIndex"), Integer("newIndex")); return true;
            case "reset": ResetTo(ConformanceRow.Expand(mutation["items"]!.AsArray()).ToArray()); return true;
            case "appendRows":
                foreach (var row in ConformanceRow.Generate(Integer("start"), Integer("count"),
                    mutation["width"] is { } width ? width.GetValue<int>() : 0))
                {
                    using var each = unit?.Invoke();
                    Add(row);
                }
                return true;
            default:
                return false;
        }
    }

    internal JsonArray ToNode() => [.. this.Select(ConformanceRow.ToNode)];
}

// An ICommand of a test View.
internal sealed class ConformanceCommand(Func<object?, bool> canExecute, Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute(parameter);

    public void Execute(object? parameter) => execute(parameter);

    internal void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

// An asynchronous command of a test View: available while it is not executing (and its own condition holds).
internal sealed class ConformanceAsyncCommand(Func<bool> canExecute, Func<object?, CancellationToken, Task<object?>> execute) : ICommand
{
    private int _executing;

    public event EventHandler? CanExecuteChanged;

    internal bool IsExecuting => Volatile.Read(ref _executing) > 0;

    public bool CanExecute(object? parameter) => !IsExecuting && canExecute();

    public void Execute(object? parameter) => _ = ExecuteAsync(parameter, CancellationToken.None);

    internal async Task<object?> ExecuteAsync(object? parameter, CancellationToken token)
    {
        Interlocked.Increment(ref _executing);
        Raise();
        try { return await execute(parameter, token).ConfigureAwait(false); }
        finally
        {
            Interlocked.Decrement(ref _executing);
            Raise();
        }
    }

    internal void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal static class ConformanceWire
{
    internal static string String(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException("Expected a string.");

    internal static void Write(Utf8JsonWriter writer, string? value)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }

    internal static Action<Utf8JsonWriter, TModel> Value<TModel>(Func<TModel, string?> read) =>
        (writer, vm) => Write(writer, read(vm));

    internal static Action<Utf8JsonWriter, TModel> Boolean<TModel>(Func<TModel, bool> read) =>
        (writer, vm) => writer.WriteBooleanValue(read(vm));

    internal static string Duration(TimeSpan value) => XmlConvert.ToString(value);

    internal static TimeSpan ReadDuration(JsonElement value)
    {
        try { return XmlConvert.ToTimeSpan(String(value)); }
        catch (Exception error) when (error is not FormatException) { throw new FormatException("Expected an ISO 8601 duration.", error); }
    }

    internal static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static DateOnly? ReadDate(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null
        : DateOnly.ParseExact(String(value), "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

// The list View: the model of the protocol-3 collection fixtures.
internal sealed class ListViewModel : ConformanceModel
{
    private string _title = "";

    public ConformanceRows Rows { get; } = [];

    public string Title
    {
        get => _title;
        set { _title = value; Raise(); }
    }

    internal static ListViewModel Create(JsonObject model)
    {
        var vm = new ListViewModel();
        if (model["rows"] is JsonArray rows) foreach (var row in ConformanceRow.Expand(rows)) vm.Rows.Add(row);
        if (model["title"] is { } title) vm._title = title.GetValue<string>();
        return vm;
    }

    internal override void Apply(JsonObject mutation, Func<IDisposable>? unit)
    {
        if (Rows.Apply(mutation, unit)) return;
        if (mutation["op"]!.GetValue<string>() == "assign" && mutation["member"]!.GetValue<string>() == "title")
            Title = mutation["value"]!.GetValue<string>();
        else throw new NotSupportedException($"The list View has no mutation {mutation.ToJsonString()}.");
    }

    internal override JsonNode? Read(string member) => member switch
    {
        "rows" => Rows.ToNode(),
        "title" => Title,
        _ => throw new NotSupportedException($"The list View has no member {member}."),
    };
}

internal sealed class ListBridge(IBridgeTransport transport, ListViewModel vm, WindowContentSession content)
    : ViewModelBridge<ListViewModel>(transport, vm, "list",
        [
            new("rows", (writer, model) => { writer.WriteStartArray(); foreach (var row in model.Rows) ConformanceRow.Write(writer, row); writer.WriteEndArray(); }),
            new("title", ConformanceWire.Value<ListViewModel>(model => model.Title)),
        ],
        [
            new("Rows", "rows", model => model.Rows, null),
            new("Title", "title", model => model.Title, (model, value) => model.Title = ConformanceWire.String(value)),
        ],
        [], [], "conformance-list", content,
        collections: [new("rows", model => model.Rows, (writer, row) => ConformanceRow.Write(writer, (ConformanceRow)row!), ConformanceRow.KeyOf)]);

// The log View: the model of the protocol-3 text fixtures.
internal sealed class LogViewModel : ConformanceModel
{
    private string? _log = "";
    private string _title = "";

    public string? Log
    {
        get => _log;
        set { _log = value; Raise(); }
    }

    public string Title
    {
        get => _title;
        set { _title = value; Raise(); }
    }

    internal static LogViewModel Create(JsonObject model)
    {
        var vm = new LogViewModel();
        if (model.TryGetPropertyValue("log", out var log)) vm._log = log?.GetValue<string>();
        if (model["title"] is { } title) vm._title = title.GetValue<string>();
        return vm;
    }

    internal override void Apply(JsonObject mutation, Func<IDisposable>? unit)
    {
        var member = mutation["member"]?.GetValue<string>();
        switch (mutation["op"]!.GetValue<string>())
        {
            case "append" when member == "log":
                Log = (Log ?? "") + mutation["text"]!.GetValue<string>();
                return;
            case "assign" when member == "log":
                Log = mutation["value"]?.GetValue<string>();
                return;
            case "assign" when member == "title":
                Title = mutation["value"]!.GetValue<string>();
                return;
            default:
                throw new NotSupportedException($"The log View has no mutation {mutation.ToJsonString()}.");
        }
    }

    internal override JsonNode? Read(string member) => member switch
    {
        "log" => Log,
        "title" => Title,
        _ => throw new NotSupportedException($"The log View has no member {member}."),
    };
}

internal sealed class LogBridge(IBridgeTransport transport, LogViewModel vm, WindowContentSession content)
    : ViewModelBridge<LogViewModel>(transport, vm, "log",
        [
            new("log", ConformanceWire.Value<LogViewModel>(model => model.Log)),
            new("title", ConformanceWire.Value<LogViewModel>(model => model.Title)),
        ],
        [
            new("Log", "log", model => model.Log, null) { AppendOnlyTextField = "log" },
            new("Title", "title", model => model.Title, (model, value) => model.Title = ConformanceWire.String(value)),
        ],
        [], [], "conformance-log", content);

internal enum NotesPriority
{
    Low,
    Normal,
    High,
}

// The detail View, the content of the notes View's detail slot.
internal sealed class DetailViewModel : ConformanceModel
{
    private string _lastAction = "";

    public DetailViewModel(NotesViewModel owner) =>
        DismissCommand = new(_ => true, _ => owner.Detail = null);

    public string LastAction
    {
        get => _lastAction;
        set { _lastAction = value; Raise(); }
    }

    public ConformanceCommand DismissCommand { get; }

    internal override void Apply(JsonObject mutation, Func<IDisposable>? unit) =>
        throw new NotSupportedException("The detail View has no mutations.");

    internal override JsonNode? Read(string member) => member == "lastAction" ? LastAction
        : throw new NotSupportedException($"The detail View has no member {member}.");
}

internal sealed class DetailBridge(IBridgeTransport transport, DetailViewModel vm, string route, WindowContentSession content)
    : ViewModelBridge<DetailViewModel>(transport, vm, route,
        [
            new("lastAction", ConformanceWire.Value<DetailViewModel>(model => model.LastAction)),
            new("canDismiss", ConformanceWire.Boolean<DetailViewModel>(model => model.DismissCommand.CanExecute(null))),
        ],
        [new("LastAction", "lastAction", model => model.LastAction, null)],
        [],
        [new CommandDescriptor<DetailViewModel>("Dismiss", model => model.DismissCommand)],
        "conformance-detail", content);

// The input of the notes View's slowImport.
internal sealed record SlowImportInput(string Source, int Limit);

// The notes View. Its operations and interaction (slowImport, confirmClear, the progress and finish mutations and
// the provided ids) are in ConformanceOperations.cs.
internal sealed partial class NotesViewModel : ConformanceModel
{
    private string _title = "";
    private string _note = "";
    private NotesPriority _priority = NotesPriority.Normal;
    private DateOnly? _due;
    private TimeSpan _elapsed;
    private DetailViewModel? _detail;

    private NotesViewModel()
    {
        AddRowCommand = new(input => !string.IsNullOrEmpty(input as string),
            input => Rows.Add(new((Rows.Count == 0 ? 0 : Rows.Max(row => row.Id)) + 1, (string)input!)));
        RemoveRowCommand = new(_ => true, row => Rows.Remove((ConformanceRow)row!));
        ClearCommand = new(_ => Rows.Count > 0, _ => Rows.ResetTo([]));
        // Appends "!" to the title without announcing the change.
        TouchCommand = new(_ => true, _ => _title += "!");
        SaveCommand = new(_ => true, _ =>
        {
            if (Title.Length == 0) throw new RunicFailureException<NotesSaveFailure>(new NotesSaveFailure.TitleRequired());
        });
        AnnotateCommand = new(_ => true, input => Detail!.LastAction = (string)input!);
        SlowImportCommand = new(() => true, async (input, token) => await SlowImportAsync((SlowImportInput)input!, token).ConfigureAwait(false));
        ConfirmClearCommand = new(() => Rows.Count > 0, async (_, token) =>
        {
            await ConfirmClearAsync(token).ConfigureAwait(false);
            return null;
        });
        Rows.CollectionChanged += (_, _) =>
        {
            ClearCommand.Raise();
            ConfirmClearCommand.Raise();
        };
    }

    public string Title
    {
        get => _title;
        set { _title = value.Trim(); Raise(); }
    }

    public string Note
    {
        get => _note;
        set { _note = value; Raise(); }
    }

    public NotesPriority Priority
    {
        get => _priority;
        set { _priority = value; Raise(); }
    }

    public DateOnly? Due
    {
        get => _due;
        set { _due = value; Raise(); }
    }

    public TimeSpan Elapsed
    {
        get => _elapsed;
        set { _elapsed = value; Raise(); }
    }

    public ConformanceRows Rows { get; } = [];

    // Neither announces a change.
    public int Count => Rows.Count;

    public string Summary => $"rows: {Count}";

    public DetailViewModel? Detail
    {
        get => _detail;
        set { _detail = value; Raise(); }
    }

    public ConformanceCommand AddRowCommand { get; }
    public ConformanceCommand RemoveRowCommand { get; }
    public ConformanceCommand ClearCommand { get; }
    public ConformanceCommand TouchCommand { get; }
    public ConformanceCommand SaveCommand { get; }
    public ConformanceCommand AnnotateCommand { get; }
    public ConformanceAsyncCommand SlowImportCommand { get; }
    public ConformanceAsyncCommand ConfirmClearCommand { get; }

    internal static NotesViewModel Create(JsonObject model)
    {
        var vm = new NotesViewModel();
        vm._detail = new DetailViewModel(vm);
        if (model["rows"] is JsonArray rows) foreach (var row in ConformanceRow.Expand(rows)) vm.Rows.Add(row);
        foreach (var (member, value) in model)
            if (member != "rows") vm.Assign(member, value);
        return vm;
    }

    private void Assign(string member, JsonNode? value)
    {
        using var document = JsonDocument.Parse(value?.ToJsonString() ?? "null");
        var element = document.RootElement;
        switch (member)
        {
            case "title": Title = ConformanceWire.String(element); break;
            case "note": Note = ConformanceWire.String(element); break;
            case "priority": Priority = NotesBridge.ReadPriority(element); break;
            case "due": Due = ConformanceWire.ReadDate(element); break;
            case "elapsed": Elapsed = ConformanceWire.ReadDuration(element); break;
            default: throw new NotSupportedException($"The notes View cannot assign {member}.");
        }
    }

    internal override void Apply(JsonObject mutation, Func<IDisposable>? unit)
    {
        if (Rows.Apply(mutation, unit)) return;
        switch (mutation["op"]!.GetValue<string>())
        {
            case "assign": Assign(mutation["member"]!.GetValue<string>(), mutation["value"]); break;
            case "progress": OnProgress(mutation); break;
            case "finish": OnFinish(mutation); break;
            default: throw new NotSupportedException($"The notes View has no mutation {mutation.ToJsonString()}.");
        }
    }

    internal override JsonNode? Read(string member) => member switch
    {
        "title" => Title,
        "note" => Note,
        "priority" => Priority.ToString(),
        "due" => ConformanceWire.Date(Due),
        "elapsed" => ConformanceWire.Duration(Elapsed),
        "rows" => Rows.ToNode(),
        "count" => Count,
        "summary" => Summary,
        _ => throw new NotSupportedException($"The notes View has no member {member}."),
    };

    internal override JsonNode? Provide(string id) => ProvideOperationId(id);

    // Seams for P5 (ConformanceOperations.cs): slowImport and confirmClear, the progress and finish mutations, the
    // provided ids and the View's interactions.
    private partial Task<int> SlowImportAsync(SlowImportInput input, CancellationToken token);
    private partial Task ConfirmClearAsync(CancellationToken token);
    partial void OnProgress(JsonObject mutation);
    partial void OnFinish(JsonObject mutation);
    private partial JsonNode? ProvideOperationId(string id);
    internal static partial BridgeInteractionDescriptor<NotesViewModel>[]? CreateInteractions();
}

// The declared failures of the notes View's commands.
internal abstract record NotesSaveFailure
{
    internal sealed record TitleRequired : NotesSaveFailure;
}

internal sealed record NotesImportFailure
{
    internal static readonly string SourceMissing = """{"$case":"sourceMissing"}""";
}

internal sealed class NotesBridge(IBridgeTransport transport, NotesViewModel vm, WindowContentSession content)
    : ViewModelBridge<NotesViewModel>(transport, vm, "notes", State(content), Properties, Checked, Commands,
        "conformance-notes", content, NotesViewModel.CreateInteractions(),
        collections: [new("rows", model => model.Rows, (writer, row) => ConformanceRow.Write(writer, (ConformanceRow)row!), ConformanceRow.KeyOf)])
{
    internal static NotesPriority ReadPriority(JsonElement value) => ConformanceWire.String(value) switch
    {
        "Low" => NotesPriority.Low,
        "Normal" => NotesPriority.Normal,
        "High" => NotesPriority.High,
        _ => throw new FormatException("Unknown priority."),
    };

    private static BridgeStateMember<NotesViewModel>[] State(WindowContentSession content) =>
    [
        new("title", ConformanceWire.Value<NotesViewModel>(model => model.Title)),
        new("note", ConformanceWire.Value<NotesViewModel>(model => model.Note)),
        new("priority", ConformanceWire.Value<NotesViewModel>(model => model.Priority.ToString())),
        new("due", ConformanceWire.Value<NotesViewModel>(model => ConformanceWire.Date(model.Due))),
        new("elapsed", ConformanceWire.Value<NotesViewModel>(model => ConformanceWire.Duration(model.Elapsed))),
        new("rows", (writer, model) => { writer.WriteStartArray(); foreach (var row in model.Rows) ConformanceRow.Write(writer, row); writer.WriteEndArray(); }),
        new("count", (writer, model) => writer.WriteNumberValue(model.Count)),
        new("summary", ConformanceWire.Value<NotesViewModel>(model => model.Summary)),
        new("detail", (writer, model) =>
        {
            if (model.Detail is not { } detail)
            {
                content.Clear(model, "Detail");
                writer.WriteNullValue();
                return;
            }
            var reference = content.Present(model, "Detail", "detail", detail,
                (detailTransport, current, route) => new DetailBridge(detailTransport, current, route, content));
            writer.WriteStartObject();
            writer.WriteString("kind", reference.Kind);
            writer.WriteString("id", reference.Id);
            writer.WriteEndObject();
        }),
        new("canClear", ConformanceWire.Boolean<NotesViewModel>(model => model.ClearCommand.CanExecute(null))),
        new("canTouch", ConformanceWire.Boolean<NotesViewModel>(model => model.TouchCommand.CanExecute(null))),
        new("canSave", ConformanceWire.Boolean<NotesViewModel>(model => model.SaveCommand.CanExecute(null))),
        new("canConfirmClear", ConformanceWire.Boolean<NotesViewModel>(model => model.ConfirmClearCommand.CanExecute(null))),
        new("isSlowImportExecuting", ConformanceWire.Boolean<NotesViewModel>(model => model.SlowImportCommand.IsExecuting)),
        new("isConfirmClearExecuting", ConformanceWire.Boolean<NotesViewModel>(model => model.ConfirmClearCommand.IsExecuting)),
    ];

    private static readonly PropertyDescriptor<NotesViewModel>[] Properties =
    [
        new("Title", "title", model => model.Title, (model, value) => model.Title = ConformanceWire.String(value)),
        new("Priority", "priority", model => model.Priority, (model, value) => model.Priority = ReadPriority(value)),
        new("Due", "due", model => model.Due, (model, value) => model.Due = ConformanceWire.ReadDate(value)),
        new("Elapsed", "elapsed", model => model.Elapsed, (model, value) => model.Elapsed = ConformanceWire.ReadDuration(value)),
        new("Rows", "rows", model => model.Rows, null),
        new("Detail", "detail", model => model.Detail, null),
    ];

    private static readonly CheckedPropertyDescriptor<NotesViewModel>[] Checked =
    [
        new("Note", "note", CheckedFieldValueKind.String, model => model.Note, (model, value) => model.Note = (string)value!),
    ];

    private static readonly CommandDescriptor<NotesViewModel>[] Commands =
    [
        new("AddRow", model => model.AddRowCommand)
        {
            ReadInput = ConformanceInput.String,
            EncodeArgument = ConformanceInput.EncodeString,
        },
        new("RemoveRow", model => model.RemoveRowCommand)
        {
            ReadInput = value => int.Parse(ConformanceWire.String(value), NumberStyles.None, CultureInfo.InvariantCulture),
            EncodeArgument = argument => JsonSerializer.Serialize(((int)argument!).ToString(CultureInfo.InvariantCulture)),
            ResolveArgument = (model, key) => model.Rows.FirstOrDefault(row => row.Id == (int)key!),
        },
        new("Clear", model => model.ClearCommand),
        new("Touch", model => model.TouchCommand),
        new("Save", model => model.SaveCommand)
        {
            EncodeFailure = failure => failure is NotesSaveFailure.TitleRequired ? """{"$case":"titleRequired"}""" : null,
        },
        new("Annotate", model => model.AnnotateCommand)
        {
            ReadInput = ConformanceInput.String,
            EncodeArgument = ConformanceInput.EncodeString,
        },
        new("SlowImport", model => model.SlowImportCommand)
        {
            ReadInput = value => new SlowImportInput(ConformanceWire.String(value.GetProperty("source")), value.GetProperty("limit").GetInt32()),
            EncodeArgument = argument => JsonSerializer.Serialize(new { source = ((SlowImportInput)argument!).Source, limit = ((SlowImportInput)argument).Limit }),
            ExecuteResultAsync = async (model, token, argument) =>
            {
                var count = (int)(await model.SlowImportCommand.ExecuteAsync(argument, token).ConfigureAwait(false))!;
                return BridgeOperationResult.Succeeded(count.ToString(CultureInfo.InvariantCulture));
            },
            EncodeFailure = failure => failure is NotesImportFailure ? NotesImportFailure.SourceMissing : null,
            Subscribe = (model, changed) => Changed(model.SlowImportCommand, changed),
        },
        new("ConfirmClear", model => model.ConfirmClearCommand)
        {
            // confirmClear ends inside its publication unit (ConformanceOperations.cs); the reply follows it.
            ExecuteAsync = async (model, token, _) =>
            {
                await model.ConfirmClearCommand.ExecuteAsync(null, token).ConfigureAwait(false);
                await Task.Yield();
            },
            Subscribe = (model, changed) => Changed(model.ConfirmClearCommand, changed),
        },
    ];

    private static Unsubscribe Changed(ICommand command, Action changed)
    {
        EventHandler handler = (_, _) => changed();
        command.CanExecuteChanged += handler;
        return new Unsubscribe(() => command.CanExecuteChanged -= handler);
    }

    private sealed class Unsubscribe(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

internal static class ConformanceInput
{
    internal static object? String(JsonElement value) => ConformanceWire.String(value);

    internal static string EncodeString(object? argument) => JsonSerializer.Serialize((string)argument!);
}
