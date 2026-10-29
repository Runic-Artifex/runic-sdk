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
public sealed class PropertyDescriptor<T>
{
    private readonly Func<T, object?> _getter;
    private readonly Action<T, JsonElement>? _setter;

    /// <summary>Describes a property whose setter reads the <c>value</c> of a <c>set</c> request.</summary>
    /// <param name="name">The .NET property name.</param>
    /// <param name="wireName">The member's wire name in state.</param>
    /// <param name="getter">Reads the property value.</param>
    /// <param name="setter">
    /// Writes the property from the request's <c>value</c>, or <see langword="null"/> for a read-only property. It throws
    /// <see cref="FormatException"/>, <see cref="JsonException"/>, <see cref="ArgumentException"/> or
    /// <see cref="OverflowException"/> for a value that is not valid input.
    /// </param>
    public PropertyDescriptor(string name, string wireName, Func<T, object?> getter, Action<T, JsonElement>? setter)
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
    /// Returns the sub-object the Bridge observes for this property. The Bridge subscribes when it is created and
    /// again when the ViewModel reports a change, and publishes when the sub-object changes. A
    /// <see cref="NavigationRegion{TContent}"/> publishes when its <c>Current</c> changes and binds the window session as
    /// its navigator's presentation; a <see cref="NavigationSelection"/> publishes when its <c>SelectedIndex</c> changes;
    /// another <see cref="INotifyPropertyChanged"/> publishes on every change.
    /// </param>
    public PropertyDescriptor(string name, string wireName, Func<T, object?> getter, Action<T, JsonElement>? setter, Func<T, object?> observe)
        : this(name, wireName, getter, setter) => Observe = observe ?? throw new ArgumentNullException(nameof(observe));

    // The sub-object whose changes the Bridge observes, if any.
    internal Func<T, object?>? Observe { get; }

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
    public Action<T, string>? TextSetter { get; init; }
    /// <summary>Whether the browser may write the property.</summary>
    public bool CanWrite => _setter is not null;
    /// <summary>Reads the property value from <paramref name="vm"/>.</summary>
    public object? Get(T vm) => _getter(vm);

    // Writes the property from a set request's value.
    internal void Set(T vm, JsonElement value)
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
public sealed class BridgeStateMember<T>(string name, Action<Utf8JsonWriter, T> write)
{
    /// <summary>The member's wire name.</summary>
    public string Name { get; } = string.IsNullOrEmpty(name) ? throw new ArgumentException("A state member needs a wire name.", nameof(name)) : name;

    /// <summary>Writes the member's value, without its name.</summary>
    public Action<Utf8JsonWriter, T> Write { get; } = write ?? throw new ArgumentNullException(nameof(write));
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
public sealed class CheckedPropertyDescriptor<T>(
    string name,
    string wireName,
    CheckedFieldValueKind valueKind,
    Func<T, object?> getter,
    Action<T, object?> setter,
    Func<JsonElement, object?>? readValue = null,
    Action<Utf8JsonWriter, object?>? writeValue = null)
{
    /// <summary>The .NET property name.</summary>
    public string Name { get; } = name;
    /// <summary>The property name on the wire.</summary>
    public string WireName { get; } = wireName;
    /// <summary>The wire value type.</summary>
    public CheckedFieldValueKind ValueKind { get; } = valueKind;
    internal Func<T, object?> Get { get; } = getter;
    internal Action<T, object?> Set { get; } = setter;
    internal Func<JsonElement, object?>? Read { get; } = readValue;
    internal Action<Utf8JsonWriter, object?>? Write { get; } = writeValue;

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

    private sealed class WireComparer(CheckedPropertyDescriptor<T> owner) : IEqualityComparer<object?>
    {
        public new bool Equals(object? left, object? right) => BridgeCanonicalJson.Equal(owner.Write!, left, right);
        public int GetHashCode(object? value) => BridgeCanonicalJson.Hash(owner.Write!, value);
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
public sealed class CommandDescriptor<T>(string name, Func<T, object> get)
{
    /// <summary>The command property name.</summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>Reads the command object from the ViewModel.</summary>
    public Func<T, object> Get { get; } = get ?? throw new ArgumentNullException(nameof(get));

    /// <summary>Executes a command without a result.</summary>
    public Func<T, CancellationToken, object?, Task>? ExecuteAsync { get; init; }

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
    public Func<T, CancellationToken, object?, Task<BridgeOperationResult>>? ExecuteResultAsync { get; init; }

    /// <summary>Encodes the argument for diagnostics and replay checks.</summary>
    public Func<object?, string>? EncodeArgument { get; init; }

    /// <summary>Evaluates whether the command can run with an argument.</summary>
    public Func<T, object?, bool>? CanExecute { get; init; }

    /// <summary>Observes changes of the command's executability.</summary>
    public Func<T, Action, IDisposable>? Subscribe { get; init; }

    /// <summary>Creates the result stream of a streaming command.</summary>
    public Func<BridgeOperationStream>? CreateStream { get; init; }

    /// <summary>Executes a streaming command.</summary>
    public Func<T, BridgeOperationExecution, CancellationToken, object?, Task<BridgeOperationResult>>? ExecuteStreamAsync { get; init; }

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
    public Func<T, Microsoft.Extensions.Logging.ILogger, IDisposable>? ObserveFailures { get; init; }

    /// <summary>
    /// Resolves the decoded wire key of a <see cref="RunicCommandKeyAttribute"/> command to the command's parameter
    /// in the Bridge call's model turn, or returns <see langword="null"/> when the key names no object.
    /// <see langword="null"/> for a command whose argument is its parameter.
    /// </summary>
    public Func<T, object?, object?>? ResolveArgument { get; init; }

    /// <summary>
    /// Resolves the decoded wire key of a <see cref="RunicCommandKeyAttribute"/> command to the command's parameter
    /// asynchronously, or returns <see langword="null"/> when the key names no object. It starts in the Bridge call's
    /// model turn; availability and execution run in a new turn. <see langword="null"/> unless the resolver is asynchronous.
    /// </summary>
    public Func<T, object?, CancellationToken, ValueTask<object?>>? ResolveArgumentAsync { get; init; }

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

    public static IDisposable Enter(Func<bool> isInactive)
    {
        var previous = IsInactive.Value;
        IsInactive.Value = isInactive;
        return new Scope(previous);
    }

    public static void ThrowIfInactive()
    {
        if (IsInactive.Value?.Invoke() == true) throw new BridgeSnapshotDetachedException();
    }

    private sealed class Scope(Func<bool>? previous) : IDisposable
    {
        private Func<bool>? _previous = previous;

        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref _previous, null);
            IsInactive.Value = previous;
        }
    }
}

internal sealed class BridgeSnapshotDetachedException : Exception
{
}

// The navigation regions a Bridge presents as content slots, in declaration order: what a guarded
// Window clears before it closes.
internal interface IBridgeRegionSlots
{
    IReadOnlyList<NavigationRegion> NavigationRegions();
}

/// <summary>Base class of generated bridges that expose one ViewModel over an <see cref="IBridgeTransport"/>.</summary>
/// <remarks>
/// A Bridge serves its route of the Views protocol 4 (specs/application/README.md): the requests in
/// BridgeRuntime.Requests.cs, the publication of its state in BridgeRuntime.Publication.cs, and the operation and
/// interaction requests in BridgeRuntime.Operations.cs.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public partial class ViewModelBridge<T> : IDisposable, IHotReloadableBridge, IBridgeDetachmentSignal,
    IBridgeSnapshotBatchParticipant, IBridgeRegionSlots where T : INotifyPropertyChanged
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
    private readonly T _vm;
    private readonly string _name;
    private static readonly string ModelName = typeof(T).Name;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    // The state members, in state order.
    private readonly BridgeStateMember<T>[] _stateMembers;
    private readonly PropertyDescriptor<T>[] _properties;
    private readonly CheckedPropertyBinding[] _checkedProperties;
    // Checked fields of a Bridge without a window, whose writes fail.
    private readonly HashSet<string> _unownedCheckedFields = new(StringComparer.Ordinal);
    private readonly CommandDescriptor<T>[] _commands;
    // Settable properties, checked fields and commands by wire name.
    private readonly Dictionary<string, PropertyDescriptor<T>> _settable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckedPropertyBinding> _checkedByMember = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CommandDescriptor<T>> _commandsByMember = new(StringComparer.Ordinal);
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
    private readonly BridgeCollectionDescriptor<T>[] _keyedCollections;
    private readonly BridgeCollectionDescriptor<T>[] _incrementalCollections;
    private readonly HashSet<string> _generatedCollectionNames;
    // Append-only text fields, which publish their appended text.
    private readonly PropertyDescriptor<T>[] _textFields;
    private readonly PropertyDescriptor<T>[] _textMembers;
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
        T vm,
        string name,
        BridgeStateMember<T>[] state,
        PropertyDescriptor<T>[] properties,
        CheckedPropertyDescriptor<T>[] checkedProperties,
        CommandDescriptor<T>[] commands,
        string? contractFingerprint = null,
        WindowContentSession? content = null,
        BridgeInteractionDescriptor<T>[]? interactions = null,
        BridgeDataSubscriptionMember[]? dataSubscriptions = null,
        BridgeCollectionDescriptor<T>[]? collections = null,
        BridgeValidationReader? validationReader = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(checkedProperties);
        ArgumentNullException.ThrowIfNull(commands);
        _transport = transport;
        _vm = vm;
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
        _keyedCollections = collections ?? [];
        _incrementalCollections = [.. _keyedCollections.Where(descriptor => descriptor.PublishesChanges)];
        _generatedCollectionNames = dataSubscriptions?.Where(member => member.EnumerateChildren is not null)
            .Select(member => member.PropertyName).ToHashSet(StringComparer.Ordinal) ?? [];
        _logger = content?.Logger ?? TraceFallbackLogger.Instance;
        _delivery = new(transport, name, _modelTurn, CaptureRequestedState, RunRequestedUnit, ModelName, _logger);
        _replyOrder = BridgeReplyOrder.For(transport);
        _name = name;
        _stateMembers = state;
        _properties = properties;
        _textFields = [.. properties.Where(property => property.AppendOnlyTextField is not null)];
        _checkedProperties = [];
        _commands = commands;
        _contractFingerprint = contractFingerprint;
        _errors = vm as INotifyDataErrorInfo;
        _validationReader = validationReader;
        foreach (var descriptor in _incrementalCollections) _incrementalMembers.Add(descriptor.Name);
        foreach (var field in _textFields) _incrementalMembers.Add(field.AppendOnlyTextField!);
        // Text members are compared as strings, not by encoding, and may change with splices.
        _textMembers = [.. properties.Where(property => property.TextMember && property.AppendOnlyTextField is null)];
        foreach (var member in _textMembers) _incrementalMembers.Add(member.WireName);

        foreach (var property in properties.Where(property => property.CanWrite))
            _settable[property.WireName] = property;
        foreach (var command in commands) _commandsByMember[command.Member] = command;

        var bindings = new List<IDisposable>();
        var subscribedCommands = new List<ICommand>();
        try
        {
            if (content is not null && interactions is not null)
                foreach (var interaction in interactions) bindings.Add(interaction.Attach(content, vm, name));
            if (dataSubscriptions is { Length: > 0 })
                bindings.Add(_dataSubscriptions = new BridgeDataSubscriptions(vm, dataSubscriptions, OnNestedChanged, TryPublishCollection, TryPublishItem,
                    rootObservedExternally: true, incrementalCollectionNames: _incrementalCollections.Select(descriptor => descriptor.Name).ToHashSet(StringComparer.Ordinal),
                    observeValidation: validationReader?.Observe, collectionChangedOutsideTurn: WarnIfOutsideModelTurn));
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
                    var contract = $"{typeof(T).FullName}:{contractFingerprint}";
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
            RunicViewsHotReload.Track(this);
            // The first unit captures the route's first state in a later model turn (CaptureFirstState).
            _delivery.RequestUnit();
        }
        catch
        {
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


    // Seam (P4 -> P5), implemented in BridgeRuntime.Operations.cs: the operation and interaction requests of the route.
    private partial ValueTask<ReadOnlyMemory<byte>> HandleOperationAsync(BridgeRouteRequest request, CancellationToken cancellationToken);

    // Seam (P4 -> P5), implemented in BridgeRuntime.Operations.cs: registers the handlers of an open as generation 0,
    // after the presentation was opened; returns the open reply's registration, or null for none.
    private partial string? RegisterOpenHandlers(string presentation, BridgeConnection connection, JsonElement handlers);

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => _modelTurn.TryRunNotification(() =>
    {
        lock (_modelGate)
        {
            if (IsInactive) return;
            RefreshCollectionSubscriptions();
            RefreshObservedSubObjects();
            // A keyed collection replaced by another instance cannot be described by changes of the old one.
            foreach (var descriptor in _incrementalCollections)
            {
                var rows = descriptor.Get(_vm);
                if (_collectionInstances.TryGetValue(descriptor, out var previous) && ReferenceEquals(previous, rows)) continue;
                _collectionInstances[descriptor] = rows;
                MarkCollectionSet(descriptor);
            }
            ScheduleUnit();
        }
    });

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

    Type IHotReloadableBridge.ContractModelType => typeof(T);

    Microsoft.Extensions.Logging.ILogger IHotReloadableBridge.Logger => _logger;

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = "Hot Reload is unavailable in trimmed applications; MetadataUpdater.IsSupported guards the reflection.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Hot Reload is unavailable in trimmed applications; MetadataUpdater.IsSupported guards the reflection.")]
    string? IHotReloadableBridge.ContractMismatch()
    {
        // The SDK ships as a Release library, so this follows whether the
        // application process can apply edits rather than how the SDK was built.
        if (!System.Reflection.Metadata.MetadataUpdater.IsSupported) return null;
        if (_contractFingerprint is not null &&
            !string.Equals(_contractFingerprint, BridgeContractShape.Compute(typeof(T)), StringComparison.Ordinal))
            return $"{typeof(T).FullName} changed its generated Bridge contract. Rebuild and restart the .NET app.";
        return null;
    }

    // A hot reload can change any member: the next publication sets every keyed collection as well.
    void IHotReloadableBridge.RefreshAfterHotReload() => OnNestedChanged();

    private void RefreshCollectionSubscriptions()
    {
        _collectionIndexes.Clear();
        var changed = false;
        foreach (var property in _properties)
        {
            if (_generatedCollectionNames.Contains(property.Name)) continue;
            var next = property.Get(_vm) as INotifyCollectionChanged;
            _collections.TryGetValue(property.Name, out var previous);
            if (ReferenceEquals(previous, next)) continue;
            changed = true;
            if (previous is not null) previous.CollectionChanged -= OnCollectionChanged;
            if (next is not null)
            {
                next.CollectionChanged += OnCollectionChanged;
                _collections[property.Name] = next;
            }
            else _collections.Remove(property.Name);
        }
        // By wire name, as BridgeDataSubscriptions reports the collections it observes.
        if (changed) Volatile.Write(ref _collectionFields, [.. _collections.Select(entry =>
            (Array.Find(_properties, property => property.Name == entry.Key)?.WireName ?? entry.Key, entry.Value))]);
    }

    private void RefreshObservedSubObjects()
    {
        foreach (var property in _properties)
        {
            if (property.Observe is not { } observe) continue;
            var next = observe(_vm);
            _observed.TryGetValue(property.Name, out var previous);
            if (ReferenceEquals(previous.Source, next)) continue;
            previous.Subscription?.Dispose();
            _observed.Remove(property.Name);
            var subscription = next switch
            {
                NavigationRegion region => NavigationPresentationBinding.Observe(region, _content, ScheduleUnit),
                NavigationSelection selection => NavigationPresentationBinding.Observe(selection, ScheduleUnit),
                INotifyPropertyChanged notifying => new SubObjectSubscription(notifying, ScheduleUnit),
                _ => null,
            };
            if (next is not null && subscription is not null) _observed[property.Name] = (next, subscription);
        }
    }

    IReadOnlyList<NavigationRegion> IBridgeRegionSlots.NavigationRegions() => _modelTurn.Run(() =>
    {
        List<NavigationRegion> regions = [];
        foreach (var property in _properties)
            if (property.Observe?.Invoke(_vm) is NavigationRegion region && !regions.Contains(region)) regions.Add(region);
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
            lease.Dispose();
            _content?.NextRevision();
        }
        _modelTurn.RunForTeardown(DisposeCore);
        Interlocked.Exchange(ref _modelContextLease, null)?.Dispose();
        GC.SuppressFinalize(this);
    }

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

    private sealed record CheckedPropertyBinding(CheckedPropertyDescriptor<T> Descriptor,
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
