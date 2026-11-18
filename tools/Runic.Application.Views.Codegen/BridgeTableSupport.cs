using System.Text;

/// <summary>
/// Descriptor tables (S4 of runic-sdk#1109): a Bridge describes its members as data, a table of records, and answers
/// for them in one <c>switch</c> per operation (<c>Members</c>, a <c>RunicBridgeMembers</c>). The assembly's generated
/// <c>RunicBridgeTable</c> reads a table once per ViewModel type and creates the descriptors that the Bridge passes to
/// <c>ViewModelBridge</c>, with each delegate bound to a <c>RunicBridgeSlot</c> (the members object and an id). So a
/// Bridge has no lambda per member and operation, and its constructor creates no descriptors.
/// </summary>
/// <remarks>
/// The reader is generated once per bridged assembly (D072.14), in <see cref="FileName"/>; the Views runtime has no
/// API for it. <see cref="Operations"/> is the one list of the switch operations: the base class and every Bridge's
/// overrides are generated from it.
/// </remarks>
internal static class BridgeTableSupport
{
    internal const string FileName = "RunicShared.Tables.Bridge.g.cs";
    internal const string MembersClass = "RunicBridgeMembers";
    internal const string TableClass = "RunicBridgeTable";

    /// <summary>Whether development-only views and members are in this application (D072.12, D072.13).</summary>
    internal const string IsSupported = "global::Runic.Application.Views.RunicDevelopmentOnly.IsSupported";

    private const string Json = "global::System.Text.Json.JsonElement";
    private const string Writer = "global::System.Text.Json.Utf8JsonWriter";
    private const string Generated = "global::Runic.Application.Views.Generated";
    private const string Tasks = "global::System.Threading.Tasks";
    private const string Token = "global::System.Threading.CancellationToken";

    /// <summary>
    /// One switch operation. A model operation's method takes the ViewModel first (<c>object model, int id</c>) and
    /// casts it once to <c>vm</c>; the others take the id first. An async one awaits in its arms.
    /// </summary>
    internal sealed record Operation(string Name, string Returns, string Parameters, bool Model = false, bool Async = false)
    {
        internal string Signature => $"{Name}({(Model ? "object model, int id" : "int id")}{(Parameters.Length == 0 ? "" : ", " + Parameters)})";
        internal bool ReturnsVoid => Returns == "void";
    }

    internal static readonly Operation[] Operations =
    [
        // Properties (P and C records) and keyed collections (L).
        new("Get", "object?", "", Model: true),
        new("Observe", "object?", "", Model: true),
        new("Set", "void", $"{Json} value", Model: true),
        new("SetValue", "void", "object? value", Model: true),
        new("ReadValue", "object?", $"{Json} element"),
        new("WriteValue", "void", $"{Writer} writer, object? value"),
        new("FieldPaths", $"global::System.Collections.Generic.IReadOnlyList<{Generated}.BridgeFieldPath>?", ""),
        new("WriteItem", "void", $"{Writer} writer, object? item"),
        new("Key", "string", "object? item"),
        new("UpdateRow", "void", "object? row, global::Runic.Application.Views.RunicRowEdit edit", Model: true),
        // State members (S).
        new("WriteState", "void", $"{Writer} writer, global::Runic.Application.Views.WindowContentSession? session", Model: true),
        // Data subscriptions (D): the members below the ViewModel's, read from their owner.
        new("ReadData", "object?", "object owner"),
        new("Enumerate", "global::System.Collections.IEnumerable?", "object value"),
        new("EnumerateValidation", $"global::System.Collections.Generic.IEnumerable<{Generated}.BridgeValidationChild>", "object value"),
        new("ValidationProperty", "global::System.Reflection.PropertyInfo?", ""),
        // Commands (K).
        new("Command", "object", "", Model: true),
        new("Execute", $"{Tasks}.Task", $"{Token} token, object? argument", Model: true),
        new("ExecuteResult", $"{Tasks}.Task<{Generated}.BridgeOperationResult>", $"{Token} token, object? argument", Model: true),
        new("ExecuteStream", $"{Tasks}.Task<{Generated}.BridgeOperationResult>", $"{Generated}.BridgeOperationExecution execution, {Token} token, object? argument", Model: true, Async: true),
        new("Start", $"{Tasks}.Task", "object command, object? argument"),
        new("EncodeResult", $"{Generated}.BridgeOperationResult", $"{Tasks}.Task execution"),
        new("ReadInput", "object?", $"{Json} input"),
        new("EncodeArgument", "string", "object? argument"),
        new("CanExecute", "bool", "object? argument", Model: true),
        new("Subscribe", "global::System.IDisposable", "global::System.Action changed", Model: true),
        new("EncodeFailure", "string?", "object failure"),
        new("ObserveFailures", "global::System.IDisposable", "global::Microsoft.Extensions.Logging.ILogger logger", Model: true),
        new("Resolve", "object?", "object? key", Model: true),
        new("ResolveAsync", $"{Tasks}.ValueTask<object?>", $"object? key, {Token} token", Model: true, Async: true),
    ];

    internal static Operation Find(string name) =>
        Operations.FirstOrDefault(operation => operation.Name == name) ?? throw new ArgumentException($"{name} is not a Bridge table operation.", nameof(name));

    /// <summary>
    /// A table field: a record's fields are separated by spaces, so a backslash, a space, a line break and a double
    /// quote (which could end the raw string literal) are escaped as <c>\\</c>, <c>\s</c>, <c>\n</c> and <c>\q</c>.
    /// </summary>
    internal static string Field(string value) => value.Length == 0 ? "\\e"
        : value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(" ", "\\s", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\"", "\\q", StringComparison.Ordinal);

    internal static string SupportFile()
    {
        var cs = new StringBuilder();
        cs.Append("""
            // <auto-generated />
            #nullable enable
            namespace Runic.Application.Views.Generated.Bridges;

            // A ViewModel's members by id, one method per operation; each Bridge's Members class overrides those it uses with
            // a switch over the ids of its table. The ids index the ViewModel's properties, state members, commands and data
            // members in the order of their table records.
            internal abstract class RunicBridgeMembers
            {

            """);
        foreach (var operation in Operations)
            cs.AppendLine($"    public virtual {operation.Returns} {operation.Signature} => throw new global::System.NotSupportedException();");
        cs.Append("""
            }

            // One member of a table and the delegates that the descriptors take, each calling the members object with the id.
            internal sealed class RunicBridgeSlot(RunicBridgeMembers members, int id)
            {
                public object? Get(object model) => members.Get(model, id);
                public object? Observe(object model) => members.Observe(model, id);
                public void Set(object model, global::System.Text.Json.JsonElement value) => members.Set(model, id, value);
                public void SetValue(object model, object? value) => members.SetValue(model, id, value);
                public void SetText(object model, string value) => members.SetValue(model, id, value);
                public object? ReadValue(global::System.Text.Json.JsonElement element) => members.ReadValue(id, element);
                public void WriteValue(global::System.Text.Json.Utf8JsonWriter writer, object? value) => members.WriteValue(id, writer, value);
                public void WriteItem(global::System.Text.Json.Utf8JsonWriter writer, object? item) => members.WriteItem(id, writer, item);
                public string Key(object? item) => members.Key(id, item);
                public void UpdateRow(object model, object? row, global::Runic.Application.Views.RunicRowEdit edit) => members.UpdateRow(model, id, row, edit);
                public object? ReadData(object owner) => members.ReadData(id, owner);
                public global::System.Collections.IEnumerable? Enumerate(object value) => members.Enumerate(id, value);
                public global::System.Collections.Generic.IEnumerable<global::Runic.Application.Views.Generated.BridgeValidationChild> EnumerateValidation(object value) => members.EnumerateValidation(id, value);
                public global::System.Reflection.PropertyInfo? ValidationProperty() => members.ValidationProperty(id);
                public object Command(object model) => members.Command(model, id);
                public global::System.Threading.Tasks.Task Execute(object model, global::System.Threading.CancellationToken token, object? argument) => members.Execute(model, id, token, argument);
                public global::System.Threading.Tasks.Task<global::Runic.Application.Views.Generated.BridgeOperationResult> ExecuteResult(object model, global::System.Threading.CancellationToken token, object? argument) => members.ExecuteResult(model, id, token, argument);
                public global::System.Threading.Tasks.Task<global::Runic.Application.Views.Generated.BridgeOperationResult> ExecuteStream(object model, global::Runic.Application.Views.Generated.BridgeOperationExecution execution, global::System.Threading.CancellationToken token, object? argument) => members.ExecuteStream(model, id, execution, token, argument);
                public object? ReadInput(global::System.Text.Json.JsonElement input) => members.ReadInput(id, input);
                public string EncodeArgument(object? argument) => members.EncodeArgument(id, argument);
                public bool CanExecute(object model, object? argument) => members.CanExecute(model, id, argument);
                public global::System.IDisposable Subscribe(object model, global::System.Action changed) => members.Subscribe(model, id, changed);
                public string? EncodeFailure(object failure) => members.EncodeFailure(id, failure);
                public global::System.IDisposable ObserveFailures(object model, global::Microsoft.Extensions.Logging.ILogger logger) => members.ObserveFailures(model, id, logger);
                public object? Resolve(object model, object? key) => members.Resolve(model, id, key);
                public global::System.Threading.Tasks.ValueTask<object?> ResolveAsync(object model, object? key, global::System.Threading.CancellationToken token) => members.ResolveAsync(model, id, key, token);
            }

            // A state member of one Bridge instance: the writers of presented content need its window session.
            internal sealed class RunicBridgeStateSlot(RunicBridgeMembers members, int id, global::Runic.Application.Views.WindowContentSession? session)
            {
                public void Write(global::System.Text.Json.Utf8JsonWriter writer, object model) => members.WriteState(model, id, writer, session);
            }

            // A ViewModel's descriptors, read once per ViewModel type from its Bridge's table. One record per line, its fields
            // separated by spaces (escapes: \\ backslash, \s space, \n line break, \q double quote, \e an empty field); "-" is
            // an absent field, and flags are letters:
            //   P name wire id flags         property. S settable (Set), T text member, X text setter (SetValue),
            //                                A append-only text field, O observed source (Observe), R region collection,
            //                                C region close, Q query rows (WriteItem)
            //   C name wire id flags         checked property (ReadValue, WriteValue, SetValue). F field paths (FieldPaths)
            //   S wire id                    state member (WriteState)
            //   K name wire id flags retirement close
            //                                command (Command). E Execute, R ExecuteResult, M ExecuteStream, I ReadInput,
            //                                A EncodeArgument, C CanExecute, U Subscribe, F EncodeFailure, O ObserveFailures,
            //                                K Resolve, Y ResolveAsync; retirement and close are the enum values
            //   L wire id flags              keyed collection (Get, WriteItem, Key). E editable rows (FieldPaths, UpdateRow),
            //                                W row conflicts
            //   D id parent wire name flags property
            //                                data member, below the record with id parent ("-" for the ViewModel's). It reads
            //                                the ViewModel's property (Get), its owner (I) or its member (ReadData). L a list,
            //                                Y a dictionary (Enumerate, EnumerateValidation), T path-transparent, V validation
            //                                property (ValidationProperty)
            // Ids index the properties (P, C, L, D's property), state members (S), commands (K) and data members (D's id).
            // A record whose kind ends in ? (P?, K?, ...) is a development-only member's ([RunicDevelopmentOnly]): without
            // RunicDevelopmentOnly.IsSupported, which a trimmed or NativeAOT publish turns off, its members and state are
            // absent and its command is unavailable.
            internal sealed class RunicBridgeTable
            {
                private readonly RunicBridgeMembers _members;
                private readonly global::System.Collections.Generic.List<string[]> _records = [];
                private RunicBridgeDescriptors? _withDevelopmentOnly;
                private RunicBridgeDescriptors? _withoutDevelopmentOnly;

                internal RunicBridgeTable(RunicBridgeMembers members, global::System.ReadOnlySpan<byte> table)
                {
                    _members = members;
                    foreach (var line in global::System.Text.Encoding.UTF8.GetString(table).Split('\n', global::System.StringSplitOptions.RemoveEmptyEntries | global::System.StringSplitOptions.TrimEntries))
                    {
                        var f = line.Split(' ');
                        for (var index = 0; index < f.Length; index++) f[index] = RunicBridgeDescriptors.Unescape(f[index]);
                        _records.Add(f);
                    }
                }

                // The descriptors with the development-only members or without them, created on first use. A publish's
                // switch is a constant, so it creates one set; a process that sets the switch before it opens a window
                // gets the matching set.
                private RunicBridgeDescriptors Current => global::Runic.Application.Views.RunicDevelopmentOnly.IsSupported
                    ? _withDevelopmentOnly ??= new(_members, _records, developmentOnly: true)
                    : _withoutDevelopmentOnly ??= new(_members, _records, developmentOnly: false);

                internal global::Runic.Application.Views.Generated.PropertyDescriptor[] Properties => Current.Properties;
                internal global::Runic.Application.Views.Generated.CheckedPropertyDescriptor[] Checked => Current.Checked;
                internal global::Runic.Application.Views.Generated.CommandDescriptor[] Commands => Current.Commands;
                internal global::Runic.Application.Views.Generated.BridgeCollectionDescriptor[] Collections => Current.Collections;
                internal global::Runic.Application.Views.Generated.BridgeDataSubscriptionMember[] Data => Current.Data;
                internal global::Runic.Application.Views.Generated.BridgeStateMember[] State => Current.State;
                internal global::Runic.Application.Views.Generated.BridgeStateMember[] CreateState(global::Runic.Application.Views.WindowContentSession? session) =>
                    Current.CreateState(session);
            }

            // One ViewModel's descriptors, with or without its development-only members.
            internal sealed class RunicBridgeDescriptors
            {
                private static readonly global::System.Func<global::Runic.Application.Views.Generated.BridgeOperationStream> CreateStream =
                    static () => new global::Runic.Application.Views.Generated.BridgeOperationStream();
                private static readonly global::System.Func<object, object?> Identity = static owner => owner;
                private static readonly global::System.Func<object, global::System.Collections.IEnumerable?> EnumerateList =
                    static value => (global::System.Collections.IEnumerable)value;
                private static readonly global::System.Func<object, global::System.Collections.Generic.IEnumerable<global::Runic.Application.Views.Generated.BridgeValidationChild>> EnumerateListValidation =
                    static value => global::Runic.Application.Views.Generated.BridgeValidation.EnumerateIndexed((global::System.Collections.IEnumerable)value);
                // A development-only command that this publish leaves out.
                private static readonly object RemovedCommand = new();

                private readonly RunicBridgeMembers _members;
                private readonly global::System.Collections.Generic.List<(string Name, int Id)> _state = [];

                internal RunicBridgeDescriptors(RunicBridgeMembers members, global::System.Collections.Generic.IReadOnlyList<string[]> records,
                    bool developmentOnly)
                {
                    _members = members;
                    var properties = new global::System.Collections.Generic.List<global::Runic.Application.Views.Generated.PropertyDescriptor>();
                    var checkedProperties = new global::System.Collections.Generic.List<global::Runic.Application.Views.Generated.CheckedPropertyDescriptor>();
                    var commands = new global::System.Collections.Generic.List<global::Runic.Application.Views.Generated.CommandDescriptor>();
                    var collections = new global::System.Collections.Generic.List<global::Runic.Application.Views.Generated.BridgeCollectionDescriptor>();
                    var data = new global::System.Collections.Generic.List<string[]>();
                    // One slot per property and per command, shared by its records.
                    var propertySlots = new global::System.Collections.Generic.Dictionary<int, RunicBridgeSlot>();
                    var commandSlots = new global::System.Collections.Generic.Dictionary<int, RunicBridgeSlot>();
                    foreach (var record in records)
                    {
                        var f = (string[])record.Clone();
                        if (f[0].EndsWith('?'))
                        {
                            f[0] = f[0][..^1];
                            if (!developmentOnly)
                            {
                                if (f[0] == "K") commands.Add(Unavailable(f));
                                continue;
                            }
                        }
                        switch (f[0])
                        {
                            case "P":
                            {
                                var slot = Slot(propertySlots, Id(f[3]));
                                var flags = f[4];
                                var setter = Has(flags, 'S') ? slot.Set : (global::System.Action<object, global::System.Text.Json.JsonElement>?)null;
                                // The same init properties on both constructors: one takes the observed source.
                                properties.Add(Has(flags, 'O')
                                    ? new(f[1], f[2], slot.Get, setter, slot.Observe)
                                    {
                                        RegionCollection = Has(flags, 'R'),
                                        RegionClose = Has(flags, 'C'),
                                        QueryRowWriter = Has(flags, 'Q') ? slot.WriteItem : null,
                                    }
                                    : new(f[1], f[2], slot.Get, setter)
                                    {
                                        AppendOnlyTextField = Has(flags, 'A') ? f[2] : null,
                                        TextMember = Has(flags, 'T'),
                                        TextSetter = Has(flags, 'X') ? slot.SetText : null,
                                    });
                                break;
                            }
                            case "C":
                            {
                                var id = Id(f[3]);
                                var slot = Slot(propertySlots, id);
                                checkedProperties.Add(new(f[1], f[2], global::Runic.Application.Views.Generated.CheckedFieldValueKind.Json, slot.Get, slot.SetValue,
                                    readValue: slot.ReadValue, writeValue: slot.WriteValue) { FieldPaths = Has(f[4], 'F') ? members.FieldPaths(id) : null });
                                break;
                            }
                            case "S":
                                _state.Add((f[1], Id(f[2])));
                                break;
                            case "K":
                            {
                                var slot = Slot(commandSlots, Id(f[3]));
                                var flags = f[4];
                                commands.Add(new(f[1], slot.Command)
                                {
                                    WireName = f[2],
                                    ExecuteAsync = Has(flags, 'E') ? slot.Execute : null,
                                    ExecuteResultAsync = Has(flags, 'R') ? slot.ExecuteResult : null,
                                    CreateStream = Has(flags, 'M') ? CreateStream : null,
                                    ExecuteStreamAsync = Has(flags, 'M') ? slot.ExecuteStream : null,
                                    ReadInput = Has(flags, 'I') ? slot.ReadInput : null,
                                    EncodeArgument = Has(flags, 'A') ? slot.EncodeArgument : null,
                                    CanExecute = Has(flags, 'C') ? slot.CanExecute : null,
                                    Subscribe = Has(flags, 'U') ? slot.Subscribe : null,
                                    EncodeFailure = Has(flags, 'F') ? slot.EncodeFailure : null,
                                    ObserveFailures = Has(flags, 'O') ? slot.ObserveFailures : null,
                                    ResolveArgument = Has(flags, 'K') ? slot.Resolve : null,
                                    ResolveArgumentAsync = Has(flags, 'Y') ? slot.ResolveAsync : null,
                                    Retirement = f[5] == "-" ? default : (global::Runic.Application.Views.RunicOperationRetirement)Id(f[5]),
                                    Close = f[6] == "-" ? default : (global::Runic.Application.Views.RunicOperationClose)Id(f[6]),
                                });
                                break;
                            }
                            case "L":
                            {
                                var id = Id(f[2]);
                                var slot = Slot(propertySlots, id);
                                var editable = Has(f[3], 'E');
                                collections.Add(new(f[1], slot.Get, slot.WriteItem, slot.Key)
                                {
                                    FieldPaths = editable ? members.FieldPaths(id) : null,
                                    UpdateRow = editable ? slot.UpdateRow : null,
                                    RowConflicts = Has(f[3], 'W') ? global::Runic.Application.Views.RunicRowConflicts.Row : default,
                                });
                                break;
                            }
                            case "D":
                                data.Add(f);
                                break;
                            default:
                                throw new global::System.FormatException($"Unknown Bridge table record '{string.Join(' ', f)}'.");
                        }
                    }
                    Properties = [.. properties];
                    Checked = [.. checkedProperties];
                    Commands = [.. commands];
                    Collections = [.. collections];
                    Data = DataMembers(data, "-", propertySlots) ?? [];
                    State = CreateState(null);
                }

                internal global::Runic.Application.Views.Generated.PropertyDescriptor[] Properties { get; }
                internal global::Runic.Application.Views.Generated.CheckedPropertyDescriptor[] Checked { get; }
                internal global::Runic.Application.Views.Generated.CommandDescriptor[] Commands { get; }
                internal global::Runic.Application.Views.Generated.BridgeCollectionDescriptor[] Collections { get; }
                internal global::Runic.Application.Views.Generated.BridgeDataSubscriptionMember[] Data { get; }

                // The state of a Bridge without presented content, whose writers need no window session.
                internal global::Runic.Application.Views.Generated.BridgeStateMember[] State { get; }

                // The state of one Bridge that presents content in its window session.
                internal global::Runic.Application.Views.Generated.BridgeStateMember[] CreateState(global::Runic.Application.Views.WindowContentSession? session)
                {
                    var state = new global::Runic.Application.Views.Generated.BridgeStateMember[_state.Count];
                    for (var index = 0; index < state.Length; index++)
                        state[index] = new(_state[index].Name, new RunicBridgeStateSlot(_members, _state[index].Id, session).Write);
                    return state;
                }

                // The data members below parent, in table order, each with its own children; null when there are none.
                private global::Runic.Application.Views.Generated.BridgeDataSubscriptionMember[]? DataMembers(
                    global::System.Collections.Generic.List<string[]> records, string parent, global::System.Collections.Generic.Dictionary<int, RunicBridgeSlot> propertySlots)
                {
                    var members = new global::System.Collections.Generic.List<global::Runic.Application.Views.Generated.BridgeDataSubscriptionMember>();
                    foreach (var f in records)
                    {
                        if (f[2] != parent) continue;
                        var slot = new RunicBridgeSlot(_members, Id(f[1]));
                        var flags = f[5];
                        var read = f[6] != "-" ? Slot(propertySlots, Id(f[6])).Get : Has(flags, 'I') ? Identity : slot.ReadData;
                        members.Add(new(f[3], read, DataMembers(records, f[1], propertySlots),
                            enumerateChildren: Has(flags, 'L') ? EnumerateList : Has(flags, 'Y') ? slot.Enumerate : null,
                            propertyName: f[4],
                            enumerateValidationChildren: Has(flags, 'L') ? EnumerateListValidation : Has(flags, 'Y') ? slot.EnumerateValidation : null,
                            isPathTransparent: Has(flags, 'T'))
                        {
                            ValidationProperty = Has(flags, 'V') ? slot.ValidationProperty : null,
                        });
                    }
                    return members.Count == 0 ? null : [.. members];
                }

                // A development-only command that this publish left out: it keeps its name and shape, so that every call
                // reaches the runtime's availability check, which rejects it as unavailable. Nothing of the command runs.
                private static global::Runic.Application.Views.Generated.CommandDescriptor Unavailable(string[] f)
                {
                    var flags = f[4];
                    global::System.Threading.Tasks.Task<global::Runic.Application.Views.Generated.BridgeOperationResult> Never() =>
                        global::System.Threading.Tasks.Task.FromException<global::Runic.Application.Views.Generated.BridgeOperationResult>(
                            new global::System.InvalidOperationException($"{f[1]} is development-only and left out of this publish."));
                    return new(f[1], static _ => RemovedCommand)
                    {
                        WireName = f[2],
                        CanExecute = static (_, _) => false,
                        ExecuteAsync = Has(flags, 'E') ? (_, _, _) => Never() : null,
                        ExecuteResultAsync = Has(flags, 'R') ? (_, _, _) => Never() : null,
                        CreateStream = Has(flags, 'M') ? CreateStream : null,
                        ExecuteStreamAsync = Has(flags, 'M') ? (_, _, _, _) => Never() : null,
                        ReadInput = Has(flags, 'I') ? static _ => null : null,
                        EncodeArgument = Has(flags, 'A') ? static _ => "null" : null,
                        Subscribe = Has(flags, 'U') ? static (_, _) => NoSubscription.Instance : null,
                        ObserveFailures = Has(flags, 'O') ? static (_, _) => NoSubscription.Instance : null,
                        ResolveArgument = Has(flags, 'K') ? static (_, key) => key : null,
                        ResolveArgumentAsync = Has(flags, 'Y') ? static (_, key, _) => global::System.Threading.Tasks.ValueTask.FromResult(key) : null,
                        Retirement = f[5] == "-" ? default : (global::Runic.Application.Views.RunicOperationRetirement)Id(f[5]),
                        Close = f[6] == "-" ? default : (global::Runic.Application.Views.RunicOperationClose)Id(f[6]),
                    };
                }

                private sealed class NoSubscription : global::System.IDisposable
                {
                    internal static readonly NoSubscription Instance = new();
                    public void Dispose() { }
                }

                private RunicBridgeSlot Slot(global::System.Collections.Generic.Dictionary<int, RunicBridgeSlot> slots, int id)
                {
                    if (!slots.TryGetValue(id, out var slot)) slots.Add(id, slot = new(_members, id));
                    return slot;
                }

                private static bool Has(string flags, char flag) => flags.Contains(flag);

                private static int Id(string field) => int.Parse(field, global::System.Globalization.CultureInfo.InvariantCulture);

                internal static string Unescape(string field)
                {
                    if (!field.Contains('\\')) return field;
                    var result = new global::System.Text.StringBuilder(field.Length);
                    for (var index = 0; index < field.Length; index++)
                    {
                        if (field[index] != '\\' || index + 1 == field.Length) { result.Append(field[index]); continue; }
                        switch (field[++index])
                        {
                            case 's': result.Append(' '); break;
                            case 'n': result.Append('\n'); break;
                            case 'q': result.Append('"'); break;
                            case 'e': break;
                            default: result.Append(field[index]); break;
                        }
                    }
                    return result.ToString();
                }
            }

            """);
        return cs.ToString();
    }
}
