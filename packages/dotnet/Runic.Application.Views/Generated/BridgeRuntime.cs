using System.Reflection;
using System.ComponentModel;
using System.Collections.Specialized;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text;
using System.Windows.Input;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Runic.Navigation;
namespace Runic.Application.Views.Generated;

// A model can be exposed through more than one Bridge. The gate must follow
// the model instance, not the individual Bridge, because synchronous
// PropertyChanged fan-out re-enters every attached Bridge.
internal static class BridgeModelGates
{
    private static readonly ConditionalWeakTable<object, object> Gates = new();

    public static object For(object model) => Gates.GetValue(model, static _ => new object());
}

// Generated descriptors and snapshot writers avoid runtime reflection.
/// <summary>Describes one bridged ViewModel property. Created by generated bridges.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PropertyDescriptor
{
    private readonly Func<object, object?> _getter;
    private readonly Action<object, JsonElement>? _setter;

    /// <summary>Describes a property whose setter reads the <c>value</c> of a <c>set</c> request.</summary>
    /// <param name="name">The .NET property name.</param>
    /// <param name="wireName">The member's wire name in state.</param>
    /// <param name="getter">Reads the property value.</param>
    /// <param name="setter">
    /// Writes the property from the request's <c>value</c>, or <see langword="null"/> for a read-only property. It throws
    /// <see cref="FormatException"/>, <see cref="JsonException"/>, <see cref="ArgumentException"/> or
    /// <see cref="OverflowException"/> for a value that is not valid input.
    /// </param>
    public PropertyDescriptor(string name, string wireName, Func<object, object?> getter, Action<object, JsonElement>? setter)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(wireName);
        Name = name;
        WireName = wireName;
        _getter = getter ?? throw new ArgumentNullException(nameof(getter));
        _setter = setter;
    }

    /// <summary>Describes a property whose value comes from an observed sub-object, such as a navigation region slot.</summary>
    /// <param name="name">The .NET property name.</param>
    /// <param name="wireName">The member's wire name in state.</param>
    /// <param name="getter">Reads the property's wire value.</param>
    /// <param name="setter">Writes the property from the request's <c>value</c>, or <see langword="null"/> for a read-only property.</param>
    /// <param name="observe">
    /// Returns the sub-object the Bridge observes for this property. The Bridge subscribes when it is created, again
    /// when the ViewModel reports a change of this property or of every property, and before the publication unit that
    /// follows a change of another property; it publishes when the sub-object changes. A
    /// <see cref="NavigationRegion{TContent}"/> publishes when its <c>Current</c> changes and binds the window session as
    /// its navigator's presentation; a <see cref="NavigationSelection"/> publishes when its <c>SelectedIndex</c> changes,
    /// and for a <see cref="RegionCollection"/> also when its regions or their <c>Current</c> change;
    /// another <see cref="INotifyPropertyChanged"/> publishes on every change.
    /// </param>
    public PropertyDescriptor(string name, string wireName, Func<object, object?> getter, Action<object, JsonElement>? setter, Func<object, object?> observe)
        : this(name, wireName, getter, setter) => Observe = observe ?? throw new ArgumentNullException(nameof(observe));

    // The sub-object whose changes the Bridge observes, if any.
    internal Func<object, object?>? Observe { get; }

    /// <summary>The .NET property name.</summary>
    public string Name { get; }
    /// <summary>The member's wire name in state.</summary>
    public string WireName { get; }
    /// <summary>
    /// The wire name of a <see cref="RunicAppendOnlyTextAttribute"/> property whose appended text the Bridge publishes
    /// as a frame, or <see langword="null"/>. <see cref="Get"/> returns its <see cref="string"/> value.
    /// </summary>
    public string? AppendOnlyTextField { get; init; }
    /// <summary>
    /// Whether the property is a text member: a <see cref="string"/> state member that frames and <c>set</c> requests may
    /// change with text splices on connections that use <c>text-splice/1</c>. <see cref="Get"/> returns its
    /// <see cref="string"/> value.
    /// </summary>
    public bool TextMember { get; init; }
    /// <summary>
    /// Writes a text member from the value a <c>set</c> request's splice produces, or from a <c>set</c> request's string
    /// value; <see langword="null"/> for a read-only property. It throws like the setter.
    /// </summary>
    public Action<object, string>? TextSetter { get; init; }
    /// <summary>
    /// Whether the property is a region collection (<see cref="RunicRegionCollectionAttribute"/>): its observed
    /// sub-object is a <see cref="NavigationSelection"/> whose regions and their content the Bridge observes, and whose
    /// regions the page selects with <c>region</c> requests (<c>region-collections/1</c>).
    /// </summary>
    public bool RegionCollection { get; init; }
    /// <summary>Whether a <c>region</c> request of a <see cref="RegionCollection"/> property may close a region.</summary>
    public bool RegionClose { get; init; }
    /// <summary>
    /// Writes one row of a <see cref="RunicQueryCollection{TRow, TKey}"/> property, whose rows a page reads with
    /// <c>query-rows</c> (<c>query-collections/1</c>); <see langword="null"/> for any other property. <see cref="Get"/>
    /// returns the collection.
    /// </summary>
    public Action<Utf8JsonWriter, object?>? QueryRowWriter { get; init; }
    /// <summary>Whether the browser may write the property.</summary>
    public bool CanWrite => _setter is not null;
    /// <summary>Reads the property value from <paramref name="vm"/>.</summary>
    public object? Get(object vm) => _getter(vm);

    // Writes the property from a set request's value.
    internal void Set(object vm, JsonElement value)
    {
        if (_setter is null) throw new InvalidOperationException($"{Name} is read-only.");
        _setter(vm, value);
    }
}

/// <summary>
/// One member of a View's state: its wire name and a writer of its value. Created by generated bridges, in state order.
/// </summary>
/// <param name="name">The member's wire name.</param>
/// <param name="write">Writes the member's value, without its name. A content slot's writer may present content.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class BridgeStateMember(string name, Action<Utf8JsonWriter, object> write)
{
    /// <summary>The member's wire name.</summary>
    public string Name { get; } = string.IsNullOrEmpty(name) ? throw new ArgumentException("A state member needs a wire name.", nameof(name)) : name;

    /// <summary>Writes the member's value, without its name.</summary>
    public Action<Utf8JsonWriter, object> Write { get; } = write ?? throw new ArgumentNullException(nameof(write));
}

// Acknowledged field writes are deliberately separate from the ordinary raw
// setter descriptor. Their registry belongs to a WindowContentSession, so a
// second presentation of the same model receives the same baseline, receipt
// history, and PropertyChanged observer.
/// <summary>The wire value type of an acknowledged field write.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public enum CheckedFieldValueKind
{
    /// <summary>A non-null string.</summary>
    String,
    /// <summary>A nullable string.</summary>
    NullableString,
    /// <summary>A 32-bit integer.</summary>
    Int32,
    /// <summary>A Boolean.</summary>
    Boolean,
    /// <summary>A value encoded by generated JSON codecs.</summary>
    Json,
}

/// <summary>Describes one property that accepts acknowledged field writes. Created by generated bridges.</summary>
/// <param name="name">The .NET property name.</param>
/// <param name="wireName">The property name on the wire.</param>
/// <param name="valueKind">The wire value type.</param>
/// <param name="getter">Reads the property value.</param>
/// <param name="setter">Writes the decoded property value.</param>
/// <param name="readValue">Decodes a <see cref="CheckedFieldValueKind.Json"/> value.</param>
/// <param name="writeValue">Encodes a <see cref="CheckedFieldValueKind.Json"/> value.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class CheckedPropertyDescriptor(
    string name,
    string wireName,
    CheckedFieldValueKind valueKind,
    Func<object, object?> getter,
    Action<object, object?> setter,
    Func<JsonElement, object?>? readValue = null,
    Action<Utf8JsonWriter, object?>? writeValue = null)
{
    /// <summary>The .NET property name.</summary>
    public string Name { get; } = name;
    /// <summary>The property name on the wire.</summary>
    public string WireName { get; } = wireName;
    /// <summary>The wire value type.</summary>
    public CheckedFieldValueKind ValueKind { get; } = valueKind;
    internal Func<object, object?> Get { get; } = getter;
    internal Action<object, object?> Set { get; } = setter;
    internal Func<JsonElement, object?>? Read { get; } = readValue;
    internal Action<Utf8JsonWriter, object?>? Write { get; } = writeValue;

    /// <summary>
    /// The paths below the member that accept checked writes with <c>field-paths/1</c>, such as <c>["street"]</c> of an
    /// address, with the generated copy of the member's value with that path changed; <see langword="null"/> when none
    /// do. A path write assigns the copy with the setter.
    /// </summary>
    public IReadOnlyList<BridgeFieldPath>? FieldPaths { get; init; }

    // A detached copy of a mutable value: decode what the generated writer wrote.
    internal object? Snapshot(object? value)
    {
        if (ValueKind is not CheckedFieldValueKind.Json) return value;
        using var raw = BridgeCanonicalJson.WriteRaw(Write!, value);
        using var document = JsonDocument.Parse(raw.WrittenMemory);
        return Read!(document.RootElement);
    }

    internal string Encode(object? value) => BridgeCanonicalJson.Encode(Write!, value);

    internal IEqualityComparer<object?> Comparer => ValueKind is CheckedFieldValueKind.Json
        ? new WireComparer(this) : EqualityComparer<object?>.Default;

    private sealed class WireComparer(CheckedPropertyDescriptor owner) : IEqualityComparer<object?>
    {
        public new bool Equals(object? left, object? right) => BridgeCanonicalJson.Equal(owner.Write!, left, right);
        public int GetHashCode(object? value) => BridgeCanonicalJson.Hash(owner.Write!, value);
    }
}

/// <summary>One field path below a member or a row (<c>field-paths/1</c>). Created by generated bridges.</summary>
/// <param name="path">The wire member names below the member or the row, such as <c>["street"]</c>.</param>
/// <param name="set">
/// Returns a copy of the object or row with the value at the path replaced by the decoded wire value, keeping every
/// member the wire does not carry. It throws <see cref="FormatException"/>, <see cref="JsonException"/>,
/// <see cref="ArgumentException"/> or <see cref="OverflowException"/> for a value that is not valid input.
/// </param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class BridgeFieldPath(string[] path, Func<object, JsonElement, object> set)
{
    /// <summary>The wire member names below the member or the row.</summary>
    public IReadOnlyList<string> Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>Copies the object or row with the value at the path replaced.</summary>
    public Func<object, JsonElement, object> Set { get; } = set ?? throw new ArgumentNullException(nameof(set));

    internal string[] Segments { get; } = path;

    /// <summary>
    /// The JSON object that <paramref name="writeObject"/> writes, with its member <paramref name="member"/> replaced
    /// by what <paramref name="writeValue"/> writes: the copy of a sealed class whose settable members are all on the
    /// wire, for its generated codec to read back.
    /// </summary>
    public static JsonDocument ReplaceMember(Action<Utf8JsonWriter> writeObject, string member, Action<Utf8JsonWriter> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeObject);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(writeValue);
        static byte[] Write(Action<Utf8JsonWriter> write)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer)) write(writer);
            return buffer.WrittenSpan.ToArray();
        }
        var owner = System.Text.Json.Nodes.JsonNode.Parse(Write(writeObject)) as System.Text.Json.Nodes.JsonObject
            ?? throw new FormatException("The value to copy is not an object.");
        owner[member] = System.Text.Json.Nodes.JsonNode.Parse(Write(writeValue));
        return JsonDocument.Parse(owner.ToJsonString());
    }
}

/// <summary>Describes one bridged command. Created by generated bridges.</summary>
/// <remarks>
/// Generated code sets the members a command needs with an object initializer, so a later release
/// can add a member without changing the constructor.
/// </remarks>
/// <param name="name">The command property name.</param>
/// <param name="get">Reads the command object from the ViewModel.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class CommandDescriptor(string name, Func<object, object> get)
{
    /// <summary>The command property name.</summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>Reads the command object from the ViewModel.</summary>
    public Func<object, object> Get { get; } = get ?? throw new ArgumentNullException(nameof(get));

    /// <summary>Executes a command without a result.</summary>
    public Func<object, CancellationToken, object?, Task>? ExecuteAsync { get; init; }

    /// <summary>The command's wire name, such as <c>save</c>; <see langword="null"/> uses <see cref="Name"/> with a lowercase first character.</summary>
    public string? WireName { get; init; }

    /// <summary>
    /// Decodes the request's <c>input</c>, or <see langword="null"/> for a command without an input. It throws
    /// <see cref="FormatException"/>, <see cref="JsonException"/>, <see cref="ArgumentException"/> or
    /// <see cref="OverflowException"/> for an input that is not valid.
    /// </summary>
    public Func<JsonElement, object?>? ReadInput { get; init; }

    // Whether the command takes an input.
    internal bool HasInput => ReadInput is not null;

    // The member name of command, can and start requests.
    internal string Member => WireName ?? char.ToLowerInvariant(Name[0]) + Name[1..];

    /// <summary>Executes a command that produces a result.</summary>
    public Func<object, CancellationToken, object?, Task<BridgeOperationResult>>? ExecuteResultAsync { get; init; }

    /// <summary>Encodes the argument for diagnostics and replay checks.</summary>
    public Func<object?, string>? EncodeArgument { get; init; }

    /// <summary>Evaluates whether the command can run with an argument.</summary>
    public Func<object, object?, bool>? CanExecute { get; init; }

    /// <summary>Observes changes of the command's executability.</summary>
    public Func<object, Action, IDisposable>? Subscribe { get; init; }

    /// <summary>Creates the result stream of a streaming command.</summary>
    public Func<BridgeOperationStream>? CreateStream { get; init; }

    /// <summary>Executes a streaming command.</summary>
    public Func<object, BridgeOperationExecution, CancellationToken, object?, Task<BridgeOperationResult>>? ExecuteStreamAsync { get; init; }

    /// <summary>
    /// Encodes the value of a <see cref="RunicFailureException"/> when it is the command's declared
    /// failure type, and returns <see langword="null"/> otherwise. <see langword="null"/> when the
    /// command declares no failure.
    /// </summary>
    public Func<object, string?>? EncodeFailure { get; init; }

    /// <summary>
    /// Observes the failures that the command reports outside a Bridge call's reply, such as a ReactiveUI command's
    /// <c>ThrownExceptions</c>, while a Bridge presents the ViewModel. The Bridge passes its logger, which writes to
    /// the Window's <see cref="Microsoft.Extensions.Logging.ILoggerFactory"/>, and disposes the result when it
    /// detaches. <see langword="null"/> when the command reports its failures only to its caller.
    /// </summary>
    public Func<object, Microsoft.Extensions.Logging.ILogger, IDisposable>? ObserveFailures { get; init; }

    /// <summary>
    /// Resolves the decoded wire key of a <see cref="RunicCommandKeyAttribute"/> command to the command's parameter
    /// in the Bridge call's model turn, or returns <see langword="null"/> when the key names no object.
    /// <see langword="null"/> for a command whose argument is its parameter.
    /// </summary>
    public Func<object, object?, object?>? ResolveArgument { get; init; }

    /// <summary>
    /// Resolves the decoded wire key of a <see cref="RunicCommandKeyAttribute"/> command to the command's parameter
    /// asynchronously, or returns <see langword="null"/> when the key names no object. It starts in the Bridge call's
    /// model turn; availability and execution run in a new turn. <see langword="null"/> unless the resolver is asynchronous.
    /// </summary>
    public Func<object, object?, CancellationToken, ValueTask<object?>>? ResolveArgumentAsync { get; init; }

    /// <summary>
    /// What happens to the command's running operations when the navigation entry whose content started them retires,
    /// from <see cref="RunicOperationRetirementAttribute"/>. <see cref="RunicOperationRetirement.Keep"/> by default.
    /// </summary>
    public RunicOperationRetirement Retirement { get; init; }
}

// Detail is present only when the window allows local failure detail.
// Failure is the encoded declared failure of a domain-failed reply, which
// never carries detail (D-1).
// Reason refines Kind with an open kebab-case token (protocol 3): "unavailable",
// "invalid-input" and "unresolved-key" (a wire key that resolves to nothing)
// under "rejected".
internal sealed record BridgeFailure(string Kind, string Message, BridgeFailureDetail? Detail = null, string? Failure = null, string? Reason = null)
{
    internal const string DomainFailed = "domain-failed";
    // .NET answers a call of a View, presentation or window it released with this kind. The
    // client-only `disconnected` (the transport is down) is never sent.
    internal const string Released = "released";
    internal const string Unavailable = "unavailable";
    internal const string InvalidInput = "invalid-input";
    internal const string UnresolvedKey = "unresolved-key";
    // The request ran, but no state including its effects can be published (invalid keys).
    internal const string StateWithheld = "state-withheld";

    internal static BridgeFailure KeyNotResolved(string command) =>
        new("rejected", $"{command} has no object for this key.", Reason: UnresolvedKey);
}

/// <summary>The Views wire protocol described in specs/application/README.md.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class BridgeProtocol
{
    /// <summary>
    /// The Views protocol version this producer implements, reported as <c>protocol</c> in every <c>hello</c> reply. A
    /// page and a producer of different protocol versions do not connect. Additive changes never change it.
    /// </summary>
    /// <remarks>
    /// Version 2 added the <c>domain-failed</c> reply error and operation status. Version 3 answers a released View
    /// <c>released</c> instead of <c>disconnected</c> and adds the reply error <c>reason</c>. Version 4 is one message
    /// channel with <c>hello</c>, frames with member <c>set</c> changes, replies by revision and edit numbers
    /// (<c>specs/application/README.md</c>).
    /// </remarks>
    public const int Version = 4;

    // The Runic.Application.Views package version, reported as "version" beside "protocol" so the browser runtime can
    // report an @runic-artifex/views package of another release (D02). Build metadata (+commit) is dropped.
    internal static string PackageVersion { get; } =
        (typeof(BridgeProtocol).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(BridgeProtocol).Assembly.GetName().Version?.ToString() ?? "0.0.0").Split('+')[0];
}

internal interface IHotReloadableBridge
{
    Type ContractModelType { get; }
    // The window session logger, or the Trace fallback without one.
    Microsoft.Extensions.Logging.ILogger Logger { get; }
    string? ContractMismatch();
    void RefreshAfterHotReload();
}

// WindowContentSession signals this before it releases bindings. A command can
// still finish truthfully after detachment, but it must not project stale
// content while the binding teardown is in flight.
internal interface IBridgeDetachmentSignal
{
    void BeginDetaching();
}

// Snapshot writers are allowed to present nested content. That presentation
// acquires WindowContentSession's gate, whereas completion holds the model
// gate. The session therefore cannot wait for this bridge while it signals
// detachment. Instead, the writer carries an ambient, lock-free cancellation
// check. WindowContentSession checks it again while holding its own gate, so a
// writer that passed the first runtime check cannot attach a route after the
// session has started detaching its parent.
internal static class BridgeSnapshotPublication
{
    private static readonly AsyncLocal<Func<bool>?> IsInactive = new();
    // The logger of the bridge whose state is being written, for the writers it calls, such as BridgeValidation.
    private static readonly AsyncLocal<Microsoft.Extensions.Logging.ILogger?> CurrentLogger = new();

    // The validation options of the window whose Bridge writes its state (WindowContentSessionOptions.Validation).
    private static readonly AsyncLocal<RunicValidationOptions?> CurrentValidation = new();

    internal static Microsoft.Extensions.Logging.ILogger Logger => CurrentLogger.Value ?? TraceFallbackLogger.Instance;

    internal static RunicValidationOptions? Validation => CurrentValidation.Value;

    public static IDisposable Enter(Func<bool> isInactive) => Enter(isInactive, null);

    public static IDisposable Enter(Func<bool> isInactive, Microsoft.Extensions.Logging.ILogger? logger) =>
        Enter(isInactive, logger, null);

    public static IDisposable Enter(Func<bool> isInactive, Microsoft.Extensions.Logging.ILogger? logger,
        RunicValidationOptions? validation)
    {
        var previous = IsInactive.Value;
        var previousLogger = CurrentLogger.Value;
        var previousValidation = CurrentValidation.Value;
        IsInactive.Value = isInactive;
        CurrentLogger.Value = logger;
        CurrentValidation.Value = validation;
        return new Scope(previous, previousLogger, previousValidation);
    }

    public static void ThrowIfInactive()
    {
        if (IsInactive.Value?.Invoke() == true) throw new BridgeSnapshotDetachedException();
    }

    private sealed class Scope(Func<bool>? previous, Microsoft.Extensions.Logging.ILogger? previousLogger,
        RunicValidationOptions? previousValidation) : IDisposable
    {
        private Func<bool>? _previous = previous;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            IsInactive.Value = Interlocked.Exchange(ref _previous, null);
            CurrentLogger.Value = previousLogger;
            CurrentValidation.Value = previousValidation;
        }
    }
}

internal sealed class BridgeSnapshotDetachedException : Exception
{
}

// The navigation regions a Bridge presents as content slots, in declaration order: what a guarded
// Window clears before it closes. A region of a region collection names its selection, so the close removes it
// instead of clearing it.
internal interface IBridgeRegionSlots
{
    IReadOnlyList<(NavigationRegion Region, NavigationSelection? Collection)> NavigationRegions();
}

/// <summary>Base class of generated bridges that expose one ViewModel over an <see cref="IBridgeTransport"/>.</summary>
/// <remarks>
/// A Bridge serves its route of the Views protocol 4 (specs/application/README.md): the requests in
/// BridgeRuntime.Requests.cs, the publication of its state in BridgeRuntime.Publication.cs, and the operation and
/// interaction requests in BridgeRuntime.Operations.cs.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public partial class ViewModelBridge : IDisposable, IHotReloadableBridge, IBridgeDetachmentSignal,
    IBridgeSnapshotBatchParticipant, IBridgeRegionSlots, IBridgeCostSource
{
    private const int MaximumFieldWriteRequestIdLength = 256;
    private readonly object _modelGate;
    private readonly BridgeSnapshotDelivery _delivery;
    private readonly BridgeModelTurn _modelTurn;
    // The replies of commands, setters and checked writes follow the pushes their requests produced (I-183).
    private readonly BridgeReplyOrder _replyOrder;
    private IRunicModelContextLease? _modelContextLease;
    private readonly WindowContentSession? _content;
    // Read from the window's options when it opened; a Bridge without a window uses the environment.
    private bool IncludesFailureDetail => _content?.IncludesFailureDetail ?? BridgeFailureDetails.DefaultIncluded;
    // The window's Bridge input limit; a bridge without a window session uses the default.
    private int MaximumInputBytes => _content?.MaximumInputBytes ?? WindowContentSessionOptions.DefaultMaximumInputBytes;
    private readonly IBridgeTransport _transport;
    // The ViewModel, held as its interface so that the runtime has one instantiation for every ViewModel type (#901).
    private readonly INotifyPropertyChanged _vm;
    private readonly string _name;
    // The ViewModel's type; never reflected over outside Hot Reload (ContractMismatch).
    private readonly Type _modelType;
    private string ModelName { get; }
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    // The state members, in state order.
    private readonly BridgeStateMember[] _stateMembers;
    private readonly PropertyDescriptor[] _properties;
    private readonly CheckedPropertyBinding[] _checkedProperties;
    // Checked fields of a Bridge without a window, whose writes fail.
    private readonly HashSet<string> _unownedCheckedFields = new(StringComparer.Ordinal);
    private readonly CommandDescriptor[] _commands;
    // Settable properties, checked fields and commands by wire name.
    private readonly Dictionary<string, PropertyDescriptor> _settable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckedPropertyBinding> _checkedByMember = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CommandDescriptor> _commandsByMember = new(StringComparer.Ordinal);
    // field-paths/1: the members with field paths below them, by wire name, and the versions of those paths.
    private readonly Dictionary<string, FieldPathTarget> _pathTargets = new(StringComparer.Ordinal);
    private readonly BridgeFieldPathTracker _fieldPaths = new();
    // Query collections (query-collections/1) by wire name.
    private readonly Dictionary<string, PropertyDescriptor> _queryCollections = new(StringComparer.Ordinal);
    private readonly ICommand[] _subscribedCommands;
    private readonly string? _contractFingerprint;
    private readonly IDisposable[] _bindings;
    // The data graph the Bridge observes; each publication unit releases what changes detached from it.
    private readonly BridgeDataSubscriptions? _dataSubscriptions;
    private readonly INotifyDataErrorInfo? _errors;
    private readonly BridgeValidationReader? _validationReader;
    private readonly Dictionary<string, INotifyCollectionChanged> _collections = new();
    // Sub-objects observed through PropertyDescriptor.Observe, by property name.
    private readonly Dictionary<string, (object Source, IDisposable Subscription)> _observed = new(StringComparer.Ordinal);
    private readonly BridgeCollectionDescriptor[] _keyedCollections;
    private readonly BridgeCollectionDescriptor[] _incrementalCollections;
    private readonly HashSet<string> _generatedCollectionNames;
    // The bridged properties and the frame-publishing keyed collections they hold, by .NET property name: a notification
    // that names a member refreshes only that member's instances (OnChanged). Keyed collections that no bridged property
    // holds are checked on every notification.
    private readonly Dictionary<string, PropertyDescriptor[]> _propertiesByName;
    private readonly Dictionary<string, BridgeCollectionDescriptor[]> _collectionsByName;
    private readonly BridgeCollectionDescriptor[] _unnamedCollections;
    // Whether a named notification arrived since every member's instances were last refreshed. A getter may return a
    // collection or sub-object that another member's notification replaced, so the next unit refreshes every member
    // once before it publishes (RefreshPendingMembers).
    private bool _memberRefreshPending;
    // Append-only text fields, which publish their appended text.
    private readonly PropertyDescriptor[] _textFields;
    private readonly PropertyDescriptor[] _textMembers;
    // The wire names of the members whose changes are published as collection or text changes, not by comparison.
    private readonly HashSet<string> _incrementalMembers = new(StringComparer.Ordinal);
    // The presentations of a Bridge without a window.
    private BridgePresentationOwners? _presentations;
    private IDisposable? _routeLease;
    private bool _disposed;
    private int _detaching;

    /// <summary>Creates the Bridge of a ViewModel whose state is written member by member.</summary>
    /// <param name="transport">The window's transport.</param>
    /// <param name="vm">The ViewModel.</param>
    /// <param name="modelType">
    /// The ViewModel's declared type, which names it in the contract, logs and telemetry; the generated Bridge passes
    /// <c>typeof</c> of its ViewModel.
    /// </param>
    /// <param name="name">The route name.</param>
    /// <param name="state">Every state member, in state order.</param>
    /// <param name="properties">The bridged properties.</param>
    /// <param name="checkedProperties">The properties with acknowledged field writes, if any.</param>
    /// <param name="commands">The bridged commands.</param>
    /// <param name="contractFingerprint">The generated contract fingerprint.</param>
    /// <param name="content">The window's content session.</param>
    /// <param name="interactions">The View's interactions.</param>
    /// <param name="dataSubscriptions">The data graph the Bridge observes.</param>
    /// <param name="collections">The keyed collections.</param>
    /// <param name="validationReader">
    /// Reads and observes the validation the ViewModel and its data graph keep outside
    /// <see cref="INotifyDataErrorInfo"/>, such as a ReactiveUI.Validation <c>ValidationContext</c>, or
    /// <see langword="null"/>. A change it observes publishes the state; checked writes report the messages it reads for
    /// their member in <c>applied.validation</c>.
    /// </param>
    protected ViewModelBridge(
        IBridgeTransport transport,
        INotifyPropertyChanged vm,
        Type modelType,
        string name,
        BridgeStateMember[] state,
        PropertyDescriptor[] properties,
        CheckedPropertyDescriptor[] checkedProperties,
        CommandDescriptor[] commands,
        string? contractFingerprint = null,
        WindowContentSession? content = null,
        BridgeInteractionDescriptor[]? interactions = null,
        BridgeDataSubscriptionMember[]? dataSubscriptions = null,
        BridgeCollectionDescriptor[]? collections = null,
        BridgeValidationReader? validationReader = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(modelType);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(checkedProperties);
        ArgumentNullException.ThrowIfNull(commands);
        _transport = transport;
        _vm = vm;
        _modelType = modelType;
        ModelName = modelType.Name;
        _modelGate = BridgeModelGates.For(vm);
        // Before the model turn is chosen: an unbound model would otherwise fall
        // back to a lock that orders differently from the window's turns.
        _modelContextLease = content?.BindBridgeModel(vm);
        _modelTurn = BridgeModelTurn.For(vm);
        _content = content;
        // The route's first state takes the window's current revision; it is not a published change. A released
        // bridge advances the window's counter (Dispose), so a new bridge of the same route starts above every
        // revision its predecessor published.
        _revision = content?.CurrentRevision ?? 0;
        _publishedRevision = _revision;
        // The first state begins the route's first epoch (presentation-epoch/1).
        _epoch = _revision;
        // Plain loops, not LINQ: the constructor runs once per Bridge and needs no iterator or grouping types.
        _keyedCollections = collections ?? [];
        var incrementalCollections = new List<BridgeCollectionDescriptor>(_keyedCollections.Length);
        foreach (var descriptor in _keyedCollections)
            if (descriptor.PublishesChanges) incrementalCollections.Add(descriptor);
        _incrementalCollections = [.. incrementalCollections];
        _generatedCollectionNames = new(StringComparer.Ordinal);
        if (dataSubscriptions is not null)
            foreach (var member in dataSubscriptions)
                if (member.EnumerateChildren is not null) _generatedCollectionNames.Add(member.PropertyName);
        _propertiesByName = GroupByName(properties);
        (_collectionsByName, _unnamedCollections) = GroupByHolder(_incrementalCollections, properties);
        _logger = content?.Logger ?? TraceFallbackLogger.Instance;
        _delivery = new(transport, name, _modelTurn, CaptureRequestedState, RunRequestedUnit, ModelName, _logger);
        _replyOrder = BridgeReplyOrder.For(transport);
        _name = name;
        _stateMembers = state;
        _properties = properties;
        var textFields = new List<PropertyDescriptor>();
        var textMembers = new List<PropertyDescriptor>();
        foreach (var property in properties)
        {
            if (property.AppendOnlyTextField is not null) textFields.Add(property);
            // Text members are compared as strings, not by encoding, and may change with splices.
            else if (property.TextMember) textMembers.Add(property);
        }
        _textFields = [.. textFields];
        _textMembers = [.. textMembers];
        _checkedProperties = [];
        _commands = commands;
        _contractFingerprint = contractFingerprint;
        _errors = vm as INotifyDataErrorInfo;
        _validationReader = validationReader;
        foreach (var descriptor in _incrementalCollections) _incrementalMembers.Add(descriptor.Name);
        foreach (var field in _textFields) _incrementalMembers.Add(field.AppendOnlyTextField!);
        foreach (var member in _textMembers) _incrementalMembers.Add(member.WireName);

        foreach (var property in properties)
        {
            if (property.CanWrite) _settable[property.WireName] = property;
            if (property.QueryRowWriter is not null) _queryCollections[property.WireName] = property;
        }
        foreach (var command in commands) _commandsByMember[command.Member] = command;
        foreach (var descriptor in checkedProperties)
            if (descriptor is { FieldPaths.Count: > 0, ValueKind: CheckedFieldValueKind.Json, Write: not null })
                _pathTargets[descriptor.WireName] = new(descriptor.WireName, descriptor.FieldPaths, descriptor, null);
        foreach (var descriptor in _keyedCollections)
            if (descriptor is { FieldPaths.Count: > 0, UpdateRow: not null })
                _pathTargets[descriptor.Name] = new(descriptor.Name, descriptor.FieldPaths, null, descriptor);

        var bindings = new List<IDisposable>();
        var subscribedCommands = new List<ICommand>();
        try
        {
            if (content is not null && interactions is not null)
                foreach (var interaction in interactions) bindings.Add(interaction.Attach(content, vm, name));
            if (dataSubscriptions is { Length: > 0 })
                bindings.Add(_dataSubscriptions = new BridgeDataSubscriptions(vm, dataSubscriptions, OnNestedChanged, TryPublishCollection, TryPublishItem,
                    rootObservedExternally: true, incrementalCollectionNames: CollectionNames(_incrementalCollections),
                    observeValidation: BridgeValidation.ObserveWithPending(validationReader?.Observe), collectionChangedOutsideTurn: WarnIfOutsideModelTurn));
            // Register the per-window observer before this Bridge subscribes
            // its publisher. A synchronous setter then advances the
            // field version before any published state can describe the value.
            if (checkedProperties.Length > 0)
            {
                if (content is null)
                {
                    // A Bridge without a window may set the property but cannot promise checked-write baselines or
                    // receipt retention: its writes fail.
                    foreach (var descriptor in checkedProperties) _unownedCheckedFields.Add(descriptor.WireName);
                }
                else
                {
                    var contract = $"{modelType.FullName}:{contractFingerprint}";
                    var checkedBindings = new List<CheckedPropertyBinding>(checkedProperties.Length);
                    foreach (var descriptor in checkedProperties)
                    {
                        // The lease shares the window's registry with other
                        // presentations of this model and releases it with
                        // the last one.
                        var field = content.FieldWrites.Acquire(
                            _vm, contract, descriptor.Name,
                            () => descriptor.Get(_vm), value => descriptor.Set(_vm, value),
                            validate: _ => CheckedFieldValidation(descriptor.Name),
                            snapshot: descriptor.Snapshot, equalityComparer: descriptor.Comparer,
                            canonicalize: value => EncodeCheckedValue(value, descriptor),
                            retainedValueByteCount: value => BridgeCanonicalJson.Length(WriteCheckedValue, (value, descriptor)));
                        bindings.Add(field.Lease);
                        var binding = new CheckedPropertyBinding(descriptor, field.Registry);
                        checkedBindings.Add(binding);
                        _checkedByMember[descriptor.WireName] = binding;
                    }
                    _checkedProperties = [.. checkedBindings];
                }
            }
            foreach (var descriptor in _incrementalCollections) _collectionInstances[descriptor] = descriptor.Get(_vm);
            _vm.PropertyChanged += OnChanged;
            RefreshCollectionSubscriptions();
            RefreshObservedSubObjects();
            if (_errors is not null) _errors.ErrorsChanged += OnErrorsChanged;
            // A rule that names no member changes neither ErrorsChanged nor a property (ReactiveValidationObject
            // raises ErrorsChanged only for the members its rules named before), so its changes are observed here.
            if (validationReader?.Observe(_vm, ScheduleUnit) is { } validation) bindings.Add(validation);
            // validation-pending/1: a check that starts or ends changes only validation.pending.
            if (BridgeValidation.ObservePending(_vm, ScheduleUnit) is { } pendingValidation) bindings.Add(pendingValidation);
            foreach (var command in _commands)
            {
                var value = command.Get(_vm);
                if (value is ICommand nativeCommand)
                {
                    nativeCommand.CanExecuteChanged += OnCanExecuteChanged;
                    subscribedCommands.Add(nativeCommand);
                }
                if (command.Subscribe is { } subscribe) bindings.Add(subscribe(_vm, ScheduleUnit));
                if (command.ObserveFailures is { } observeFailures) bindings.Add(observeFailures(_vm, _logger));
            }
            _bindings = [.. bindings];
            _subscribedCommands = [.. subscribedCommands];
            // Last: requests can arrive as soon as the route is served.
            _routeLease = BridgeDispatcher.For(transport).RegisterRoute(name, new RouteHandler(this));
            // RunicPresentation finds the routes of the model.
            BridgePresentationRegistry.Add(vm, this);
            RunicViewsHotReload.Track(this);
            // The first unit captures the route's first state in a later model turn (CaptureFirstState).
            _delivery.RequestUnit();
        }
        catch
        {
            BridgePresentationRegistry.Remove(vm, this);
            _routeLease?.Dispose();
            foreach (var binding in bindings) binding.Dispose();
            ReleaseObservedSubObjects();
            _vm.PropertyChanged -= OnChanged;
            foreach (var collection in _collections.Values) collection.CollectionChanged -= OnCollectionChanged;
            _collections.Clear();
            if (_errors is not null) _errors.ErrorsChanged -= OnErrorsChanged;
            foreach (var command in subscribedCommands) command.CanExecuteChanged -= OnCanExecuteChanged;
            _delivery.Dispose();
            _modelContextLease?.Dispose();
            throw;
        }
    }


    // The bridged properties by .NET property name, in declaration order.
    private static Dictionary<string, PropertyDescriptor[]> GroupByName(PropertyDescriptor[] properties)
    {
        var lists = new Dictionary<string, List<PropertyDescriptor>>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (!lists.TryGetValue(property.Name, out var list)) lists[property.Name] = list = [];
            list.Add(property);
        }
        var groups = new Dictionary<string, PropertyDescriptor[]>(lists.Count, StringComparer.Ordinal);
        foreach (var (name, list) in lists) groups[name] = [.. list];
        return groups;
    }

    // The frame-publishing keyed collections by the .NET name of each property whose wire name is the collection's, and
    // the collections that no property holds.
    private static (Dictionary<string, BridgeCollectionDescriptor[]> ByHolder, BridgeCollectionDescriptor[] Unnamed) GroupByHolder(
        BridgeCollectionDescriptor[] collections, PropertyDescriptor[] properties)
    {
        var lists = new Dictionary<string, List<BridgeCollectionDescriptor>>(StringComparer.Ordinal);
        var unnamed = new List<BridgeCollectionDescriptor>();
        foreach (var descriptor in collections)
        {
            var held = false;
            foreach (var property in properties)
            {
                if (!string.Equals(property.WireName, descriptor.Name, StringComparison.Ordinal)) continue;
                held = true;
                if (!lists.TryGetValue(property.Name, out var list)) lists[property.Name] = list = [];
                // Once per holder, however many of its descriptors carry the wire name.
                if (list.Count == 0 || !ReferenceEquals(list[^1], descriptor)) list.Add(descriptor);
            }
            if (!held) unnamed.Add(descriptor);
        }
        var byHolder = new Dictionary<string, BridgeCollectionDescriptor[]>(lists.Count, StringComparer.Ordinal);
        foreach (var (holder, list) in lists) byHolder[holder] = [.. list];
        return (byHolder, [.. unnamed]);
    }

    private static HashSet<string> CollectionNames(BridgeCollectionDescriptor[] collections)
    {
        var names = new HashSet<string>(collections.Length, StringComparer.Ordinal);
        foreach (var descriptor in collections) names.Add(descriptor.Name);
        return names;
    }

    // Seam (P4 -> P5), implemented in BridgeRuntime.Operations.cs: the operation and interaction requests of the route.
    private partial ValueTask<ReadOnlyMemory<byte>> HandleOperationAsync(BridgeRouteRequest request, CancellationToken cancellationToken);

    // Seam (P4 -> P5), implemented in BridgeRuntime.Operations.cs: registers the handlers of an open as generation 0,
    // after the presentation was opened; returns the open reply's registration, or null for none.
    private partial string? RegisterOpenHandlers(string presentation, BridgeConnection connection, JsonElement handlers);

    // A notification that names a member refreshes only that member's collection subscription, observed sub-object and
    // keyed collection instance, so a burst of n notifications reads n members' getters, not n times every member's
    // (#887); the unit it requests refreshes every member once (RefreshPendingMembers). An empty or null name (every
    // member changed) refreshes every member at once.
    private void OnChanged(object? sender, PropertyChangedEventArgs e) => _modelTurn.TryRunNotification(() =>
    {
        lock (_modelGate)
        {
            if (IsInactive) return;
            if (string.IsNullOrEmpty(e.PropertyName)) RefreshMembers();
            else RefreshMember(e.PropertyName);
            ScheduleUnit();
        }
    });

    // Refreshes every member's instances; the model gate is held, in a model turn.
    private void RefreshMembers()
    {
        _memberRefreshPending = false;
        RefreshCollectionSubscriptions();
        RefreshObservedSubObjects();
        foreach (var descriptor in _incrementalCollections) RefreshCollectionInstance(descriptor);
    }

    // Refreshes the instances of the member a notification named; the model gate is held, in a model turn.
    private void RefreshMember(string name)
    {
        _memberRefreshPending = true;
        // A row's index may have changed with the member.
        _collectionIndexes.Clear();
        if (_propertiesByName.TryGetValue(name, out var properties))
        {
            var changed = false;
            foreach (var property in properties) changed |= RefreshCollectionSubscription(property);
            if (changed) PublishCollectionFields();
            foreach (var property in properties) RefreshObservedSubObject(property);
        }
        if (_collectionsByName.TryGetValue(name, out var collections))
            foreach (var descriptor in collections) RefreshCollectionInstance(descriptor);
        foreach (var descriptor in _unnamedCollections) RefreshCollectionInstance(descriptor);
    }

    // Before a unit or a state reads the members: every member's instances, when a named notification may have
    // replaced one it did not name. The data graph observes the instances found as well, whatever member was notified
    // (#891), and releases the ones they replaced.
    private void RefreshPendingMembers()
    {
        if (!_memberRefreshPending) return;
        RefreshMembers();
        if (_dataSubscriptions is not { } graph) return;
        graph.RefreshRootInstances();
        graph.SweepPending();
    }

    // A keyed collection replaced by another instance cannot be described by changes of the old one. The data graph
    // observes the new instance from now on, so its changes are published after this set.
    private void RefreshCollectionInstance(BridgeCollectionDescriptor descriptor)
    {
        var rows = descriptor.Get(_vm);
        if (_collectionInstances.TryGetValue(descriptor, out var previous) && ReferenceEquals(previous, rows)) return;
        _collectionInstances[descriptor] = rows;
        MarkCollectionSet(descriptor);
        _dataSubscriptions?.Reattach(descriptor.Name);
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is INotifyCollectionChanged changed && _modelTurn.IsOutsideContextTurn
            && Array.Find(Volatile.Read(ref _collectionFields), entry => ReferenceEquals(entry.Collection, changed)).Field is { } field)
            WarnIfOutsideModelTurn(field);
        if (sender is not INotifyCollectionChanged collection || !TryPublishCollection(collection, e)) ScheduleUnit();
    }

    // The observed collections by field, replaced whole in a model turn, so a notification outside the turn can read it.
    private (string Field, INotifyCollectionChanged Collection)[] _collectionFields = [];

    // The fields whose change outside a model turn was logged (event 1025), once per Bridge and field.
    private readonly HashSet<string> _offTurnFields = new(StringComparer.Ordinal);

    // A collection that changes outside the model context's turn, such as a DynamicData Bind without ObserveOn the
    // window's scheduler, makes the notifying thread wait for a turn while the collection may change under the window.
    private void WarnIfOutsideModelTurn(string field)
    {
        if (!_modelTurn.IsOutsideContextTurn || IsInactive) return;
        bool first;
        lock (_offTurnFields) first = _offTurnFields.Add(field);
        if (first) ViewsLog.CollectionChangedOutsideModelTurn(_logger, ModelName, field, _name);
    }

    private void OnErrorsChanged(object? sender, DataErrorsChangedEventArgs e) => ScheduleUnit();

    // The applied receipt's validation (specs/application/README.md, "Checked writes"): the member's messages after
    // the write, one per line, or null when it has none. It runs in the write's model turn.
    private string? CheckedFieldValidation(string property)
    {
        IEnumerable<string> messages;
        if (_errors is not null)
            messages = (_errors.GetErrors(property) ?? Array.Empty<object>()).Cast<object?>().Select(BridgeValidation.MessageText);
        else if (_validationReader?.ReadErrors(_vm) is { } errors)
            messages = errors.OfType<ValidationResult>()
                .Where(result => result.MemberNames.Contains(property, StringComparer.Ordinal))
                .Select(result => BridgeValidation.MessageText(result.ErrorMessage));
        else return null;
        var text = string.Join("\n", messages.Distinct(StringComparer.Ordinal));
        return text.Length == 0 ? null : text;
    }
    private void OnCanExecuteChanged(object? sender, EventArgs e) => ScheduleUnit();

    // An object nested in the data graph changed. A nested change inside a row cannot be located, so each keyed
    // collection becomes a set of the field.
    private void OnNestedChanged() => _modelTurn.TryRunNotification(() =>
    {
        lock (_modelGate)
        {
            if (IsInactive) return;
            foreach (var descriptor in _incrementalCollections) MarkCollectionSet(descriptor);
            ScheduleUnit();
        }
    });

    Type IHotReloadableBridge.ContractModelType => _modelType;

    Microsoft.Extensions.Logging.ILogger IHotReloadableBridge.Logger => _logger;

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2077",
        Justification = "Hot Reload is unavailable in trimmed applications; MetadataUpdater.IsSupported guards the reflection.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Hot Reload is unavailable in trimmed applications; MetadataUpdater.IsSupported guards the reflection.")]
    string? IHotReloadableBridge.ContractMismatch()
    {
        // The SDK ships as a Release library, so this follows whether the
        // application process can apply edits rather than how the SDK was built.
        if (!System.Reflection.Metadata.MetadataUpdater.IsSupported) return null;
        if (_contractFingerprint is not null &&
            !string.Equals(_contractFingerprint, BridgeContractShape.Compute(_modelType), StringComparison.Ordinal))
            return $"{_modelType.FullName} changed its generated Bridge contract. Rebuild and restart the .NET app.";
        return null;
    }

    // A hot reload can change any member: the next publication sets every keyed collection as well.
    void IHotReloadableBridge.RefreshAfterHotReload() => OnNestedChanged();

    private void RefreshCollectionSubscriptions()
    {
        _collectionIndexes.Clear();
        var changed = false;
        foreach (var property in _properties) changed |= RefreshCollectionSubscription(property);
        if (changed) PublishCollectionFields();
    }

    // Subscribes the collection a property holds now; returns whether it changed.
    private bool RefreshCollectionSubscription(PropertyDescriptor property)
    {
        if (_generatedCollectionNames.Contains(property.Name)) return false;
        var next = property.Get(_vm) as INotifyCollectionChanged;
        _collections.TryGetValue(property.Name, out var previous);
        if (ReferenceEquals(previous, next)) return false;
        if (previous is not null) previous.CollectionChanged -= OnCollectionChanged;
        if (next is not null)
        {
            next.CollectionChanged += OnCollectionChanged;
            _collections[property.Name] = next;
        }
        else _collections.Remove(property.Name);
        return true;
    }

    // By wire name, as BridgeDataSubscriptions reports the collections it observes.
    private void PublishCollectionFields() => Volatile.Write(ref _collectionFields, [.. _collections.Select(entry =>
        (Array.Find(_properties, property => property.Name == entry.Key)?.WireName ?? entry.Key, entry.Value))]);

    private void RefreshObservedSubObjects()
    {
        foreach (var property in _properties) RefreshObservedSubObject(property);
    }

    private void RefreshObservedSubObject(PropertyDescriptor property)
    {
        if (property.Observe is not { } observe) return;
        var next = observe(_vm);
        _observed.TryGetValue(property.Name, out var previous);
        if (ReferenceEquals(previous.Source, next)) return;
        previous.Subscription?.Dispose();
        _observed.Remove(property.Name);
        // A query collection publishes in this ViewModel's model turns (query-collections/1).
        if (next is IBridgeQueryCollection query) query.Bind(_modelContextLease?.Context ?? _content?.ModelContext, _logger);
        var subscription = next switch
        {
            NavigationRegion region => NavigationPresentationBinding.Observe(region, _content, ScheduleUnit),
            NavigationSelection selection when property.RegionCollection =>
                NavigationPresentationBinding.ObserveCollection(selection, _content, ScheduleUnit),
            NavigationSelection selection => NavigationPresentationBinding.Observe(selection, ScheduleUnit),
            INotifyPropertyChanged notifying => new SubObjectSubscription(notifying, ScheduleUnit),
            _ => null,
        };
        if (next is not null && subscription is not null) _observed[property.Name] = (next, subscription);
    }

    IReadOnlyList<(NavigationRegion Region, NavigationSelection? Collection)> IBridgeRegionSlots.NavigationRegions() => _modelTurn.Run(() =>
    {
        List<(NavigationRegion Region, NavigationSelection? Collection)> regions = [];
        foreach (var property in _properties)
        {
            var observed = property.Observe?.Invoke(_vm);
            if (observed is NavigationRegion region && !regions.Exists(entry => entry.Region == region)) regions.Add((region, null));
            // A region collection's regions, in order (region-collections/1).
            else if (property.RegionCollection && observed is NavigationSelection selection)
                foreach (var member in selection.Regions)
                    if (!regions.Exists(entry => entry.Region == member)) regions.Add((member, selection));
        }
        return regions;
    });

    private void ReleaseObservedSubObjects()
    {
        foreach (var (_, subscription) in _observed.Values) subscription.Dispose();
        _observed.Clear();
    }

    private sealed class SubObjectSubscription : IDisposable
    {
        private readonly INotifyPropertyChanged _source;
        private readonly PropertyChangedEventHandler _handler;

        public SubObjectSubscription(INotifyPropertyChanged source, Action changed)
        {
            _source = source;
            _handler = (_, _) => changed();
            _source.PropertyChanged += _handler;
        }

        public void Dispose() => _source.PropertyChanged -= _handler;
    }

    /// <summary>Stops serving the Bridge's route and observing the ViewModel.</summary>
    public virtual void Dispose()
    {
        if (Interlocked.Exchange(ref _routeLease, null) is { } lease)
        {
            BridgePresentationRegistry.Remove(_vm, this);
            lease.Dispose();
            _content?.NextRevision();
        }
        _modelTurn.RunForTeardown(DisposeCore);
        // blobs/1: the URLs of the blobs the route encoded stop working.
        _blobs?.ReleaseRoute(this);
        Interlocked.Exchange(ref _modelContextLease, null)?.Dispose();
        GC.SuppressFinalize(this);
    }

    // blobs/1: the Window's blobs and uploads, which the route's codecs reach through BridgeBlobScope.
    private BridgeBlobs Blobs => _blobs ??= BridgeDispatcher.For(_transport).Blobs;
    private BridgeBlobs? _blobs;

    // Enters the scope of the route's encodes and decodes; connection is the key of the request's connection, or null
    // outside a request.
    private BridgeBlobScope.Restore EnterBlobs(string? connection) => BridgeBlobScope.Enter(Blobs, this, connection);

    private void DisposeCore()
    {
        lock (_modelGate)
        {
            if (_disposed) return;
            _disposed = true;
            _delivery.Dispose();
            ReleaseFirstState();
            _vm.PropertyChanged -= OnChanged;
            foreach (var collection in _collections.Values) collection.CollectionChanged -= OnCollectionChanged;
            _collections.Clear();
            if (_errors is not null) _errors.ErrorsChanged -= OnErrorsChanged;
            foreach (var command in _subscribedCommands)
                command.CanExecuteChanged -= OnCanExecuteChanged;
            foreach (var binding in _bindings) binding.Dispose();
            ReleaseObservedSubObjects();
        }
    }

    void IBridgeDetachmentSignal.BeginDetaching() => Interlocked.Exchange(ref _detaching, 1);

    private bool IsInactive => _disposed || Volatile.Read(ref _detaching) != 0;

    // A member with field paths: a settable object member (Nested) or a keyed collection with a row editor (Rows).
    private sealed class FieldPathTarget(string member, IReadOnlyList<BridgeFieldPath> paths, CheckedPropertyDescriptor? nested,
        BridgeCollectionDescriptor? rows)
    {
        private readonly Dictionary<string, int> _indexes = Indexes(paths);
        public string Member { get; } = member;
        public IReadOnlyList<BridgeFieldPath> Paths { get; } = paths;
        public string[][] Segments { get; } = SegmentsOf(paths);
        public CheckedPropertyDescriptor? Nested { get; } = nested;
        public BridgeCollectionDescriptor? Rows { get; } = rows;
        // The index of a declared path, or -1.
        public int IndexOf(string[] path) => _indexes.TryGetValue(BridgeFieldPathTracker.PathKey(path), out var index) ? index : -1;

        private static Dictionary<string, int> Indexes(IReadOnlyList<BridgeFieldPath> paths)
        {
            var indexes = new Dictionary<string, int>(paths.Count, StringComparer.Ordinal);
            for (var index = 0; index < paths.Count; index++) indexes.Add(BridgeFieldPathTracker.PathKey(paths[index].Segments), index);
            return indexes;
        }

        private static string[][] SegmentsOf(IReadOnlyList<BridgeFieldPath> paths)
        {
            var segments = new string[paths.Count][];
            for (var index = 0; index < paths.Count; index++) segments[index] = paths[index].Segments;
            return segments;
        }
    }

    private sealed record CheckedPropertyBinding(CheckedPropertyDescriptor Descriptor,
        BridgeFieldWriteRegistry<object?> Registry);
}

/// <summary>JSON helpers called by generated bridges.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class BridgeJson
{
    /// <summary>Decodes a JSON string value.</summary>
    /// <exception cref="FormatException"><paramref name="json"/> is not a JSON string.</exception>
    public static string ReadRequiredString(string json) =>
        ReadNullableString(json) ?? throw new FormatException("Expected a string.");

    /// <summary>Decodes a JSON string or <c>null</c> value.</summary>
    /// <exception cref="FormatException"><paramref name="json"/> is neither a string nor <c>null</c>.</exception>
    public static string? ReadNullableString(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => document.RootElement.GetString(),
            _ => throw new FormatException("Expected a string or null.")
        };
    }

    /// <summary>Writes the validation errors of <paramref name="propertyName"/> as a JSON string array named <paramref name="jsonName"/>.</summary>
    public static void WriteErrors(Utf8JsonWriter writer, INotifyDataErrorInfo viewModel,
        string propertyName, string jsonName)
    {
        writer.WritePropertyName(jsonName);
        writer.WriteStartArray();
        if (viewModel.GetErrors(propertyName) is { } errors)
        {
            foreach (var error in errors) writer.WriteStringValue(BridgeValidation.MessageText(error));
        }
        writer.WriteEndArray();
    }
}
