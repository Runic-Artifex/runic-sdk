using System.ComponentModel;
using System.Collections.Specialized;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text;
using System.Windows.Input;
using System.Diagnostics;
using System.Runtime.CompilerServices;
namespace Runic.Application.Views;

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
/// <param name="name">The .NET property name.</param>
/// <param name="getter">Reads the property value.</param>
/// <param name="setter">Writes the property from browser arguments, or <see langword="null"/> for a read-only property.</param>
public sealed class PropertyDescriptor<T>(string name, Func<T, object?> getter, Action<T, IBridgeArguments>? setter)
{
    /// <summary>The .NET property name.</summary>
    public string Name { get; } = name;
    /// <summary>Whether the browser may write the property.</summary>
    public bool CanWrite => setter is not null;
    /// <summary>Reads the property value from <paramref name="vm"/>.</summary>
    public object? Get(T vm) => getter(vm);

    internal void Set(T vm, IBridgeArguments e)
    {
        if (setter is null) throw new InvalidOperationException($"{Name} is read-only.");
        setter(vm, e);
    }
}

// Acknowledged field writes are deliberately separate from the ordinary raw
// setter descriptor. Their registry belongs to a WindowContentSession, so a
// second presentation of the same model receives the same baseline, receipt
// history, and PropertyChanged observer.
/// <summary>The wire value type of an acknowledged field write.</summary>
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
/// <param name="ReadValue">Decodes a <see cref="CheckedFieldValueKind.Json"/> value.</param>
/// <param name="WriteValue">Encodes a <see cref="CheckedFieldValueKind.Json"/> value.</param>
public sealed class CheckedPropertyDescriptor<T>(
    string name,
    string wireName,
    CheckedFieldValueKind valueKind,
    Func<T, object?> getter,
    Action<T, object?> setter,
    Func<JsonElement, object?>? ReadValue = null,
    Action<Utf8JsonWriter, object?>? WriteValue = null)
{
    /// <summary>The .NET property name.</summary>
    public string Name { get; } = name;
    /// <summary>The property name on the wire.</summary>
    public string WireName { get; } = wireName;
    /// <summary>The wire value type.</summary>
    public CheckedFieldValueKind ValueKind { get; } = valueKind;
    internal Func<T, object?> Get { get; } = getter;
    internal Action<T, object?> Set { get; } = setter;
    internal Func<JsonElement, object?>? Read { get; } = ReadValue;
    internal Action<Utf8JsonWriter, object?>? Write { get; } = WriteValue;

    internal object? Snapshot(object? value)
    {
        if (ValueKind is not CheckedFieldValueKind.Json) return value;
        using var document = JsonDocument.Parse(Encode(value));
        return Read!(document.RootElement);
    }

    internal string Encode(object? value) => BridgeWire.EncodeCanonical(writer => Write!(writer, value));

    internal IEqualityComparer<object?> Comparer => ValueKind is CheckedFieldValueKind.Json
        ? new WireComparer(this) : EqualityComparer<object?>.Default;

    private sealed class WireComparer(CheckedPropertyDescriptor<T> owner) : IEqualityComparer<object?>
    {
        public new bool Equals(object? left, object? right) => owner.Encode(left) == owner.Encode(right);
        public int GetHashCode(object? value) => StringComparer.Ordinal.GetHashCode(owner.Encode(value));
    }
}

// Generated snapshot writers call the supplied callback while their JSON
// object is still open. The base Bridge uses it for hidden per-field versions;
// generated public State interfaces never expose those versions.
/// <summary>Writes a ViewModel snapshot. Implemented by generated bridges.</summary>
/// <param name="writer">The JSON writer.</param>
/// <param name="viewModel">The ViewModel to write.</param>
/// <param name="revision">The snapshot revision.</param>
/// <param name="writeFieldMetadata">Writes field-write metadata while the snapshot object is open.</param>
public delegate void BridgeSnapshotWriter<T>(Utf8JsonWriter writer, T viewModel,
    long revision, Action<Utf8JsonWriter> writeFieldMetadata);

/// <summary>Describes one bridged command. Created by generated bridges.</summary>
/// <param name="Name">The command property name.</param>
/// <param name="Get">Reads the command object from the ViewModel.</param>
/// <param name="ExecuteAsync">Executes a command without a result.</param>
/// <param name="ReadArgument">Decodes the browser argument.</param>
/// <param name="ExecuteResultAsync">Executes a command that produces a result.</param>
/// <param name="EncodeArgument">Encodes the argument for diagnostics and replay checks.</param>
/// <param name="CanExecute">Evaluates whether the command can run with an argument.</param>
/// <param name="Subscribe">Observes changes of the command's executability.</param>
/// <param name="CreateStream">Creates the result stream of a streaming command.</param>
/// <param name="ExecuteStreamAsync">Executes a streaming command.</param>
public sealed record CommandDescriptor<T>(
    string Name,
    Func<T, object> Get,
    Func<T, CancellationToken, object?, Task>? ExecuteAsync = null,
    Func<IBridgeArguments, object?>? ReadArgument = null,
    Func<T, CancellationToken, object?, Task<BridgeOperationResult>>? ExecuteResultAsync = null,
    Func<object?, string>? EncodeArgument = null,
    Func<T, object?, bool>? CanExecute = null,
    Func<T, Action, IDisposable>? Subscribe = null,
    Func<BridgeOperationStream>? CreateStream = null,
    Func<T, BridgeOperationExecution, CancellationToken, object?, Task<BridgeOperationResult>>? ExecuteStreamAsync = null);

internal sealed record BridgeFailure(string Kind, string Message);

/// <summary>The Views wire protocol described in specs/application/README.md.</summary>
public static class BridgeProtocol
{
    /// <summary>
    /// The protocol version reported as <c>protocol</c> in every snapshot-route reply.
    /// It changes only when a generated client built for the previous version could
    /// misread a reply.
    /// </summary>
    public const int Version = 1;
}

internal interface IHotReloadableBridge
{
    Type ContractModelType { get; }
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

/// <summary>Base class of generated bridges that expose one ViewModel over an <see cref="IBridgeTransport"/>.</summary>
public class ViewModelBridge<T> : IDisposable, IHotReloadableBridge, IBridgeDetachmentSignal,
    IBridgeSnapshotBatchParticipant where T : INotifyPropertyChanged
{
    private const int MaximumFieldWritePayloadLength = 64 * 1024;
    private const int MaximumFieldWriteRequestIdLength = 256;
    private readonly object _modelGate;
    private readonly BridgeSnapshotDelivery _delivery;
    private readonly BridgeModelTurn _modelTurn;
    private readonly WindowContentSession? _content;
    private readonly IBridgeTransport _transport;
    private readonly T _vm;
    private readonly string _name;
    private readonly Action<Utf8JsonWriter, T, long> _writeSnapshot;
    private readonly BridgeSnapshotWriter<T>? _writeSnapshotWithFields;
    private readonly PropertyDescriptor<T>[] _properties;
    private readonly CheckedPropertyBinding[] _checkedProperties;
    private readonly CommandDescriptor<T>[] _commands;
    private readonly ICommand[] _subscribedCommands;
    private readonly string? _contractFingerprint;
    private readonly IDisposable[] _bindings;
    private readonly INotifyDataErrorInfo? _errors;
    private readonly Dictionary<string, INotifyCollectionChanged> _collections = new();
    private readonly BridgeCollectionDescriptor<T>[] _incrementalCollections;
    private readonly HashSet<string> _generatedCollectionNames;
    private readonly List<BridgeCollectionChange<T>> _pendingCollectionChanges = [];
    private readonly Dictionary<BridgeCollectionDescriptor<T>, Dictionary<object, int>> _collectionIndexes = [];
    private long _publishedRevision;
    private bool _requiresSnapshot;
    private long _revision;
    private bool _disposed;
    private int _detaching;

    /// <summary>Creates a bridge whose snapshot writer does not emit field-write metadata.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "Generated bridges call these constructors and are regenerated with the package; the optional descriptor arrays grow per feature.")]
    protected ViewModelBridge(
        IBridgeTransport transport,
        T vm,
        string name,
        Action<Utf8JsonWriter, T, long> writeSnapshot,
        PropertyDescriptor<T>[] properties,
        CommandDescriptor<T>[] commands,
        string? contractFingerprint = null,
        WindowContentSession? content = null,
        BridgeInteractionDescriptor<T>[]? interactions = null,
        BridgeDataSubscriptionMember[]? dataSubscriptions = null,
        BridgeCollectionDescriptor<T>[]? collections = null)
        : this(transport, vm, name, writeSnapshot, null, properties, [], commands,
            contractFingerprint, content, interactions, dataSubscriptions, collections)
    {
    }

    /// <summary>Creates a bridge with acknowledged field writes.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "Generated bridges call these constructors and are regenerated with the package; the optional descriptor arrays grow per feature.")]
    protected ViewModelBridge(
        IBridgeTransport transport,
        T vm,
        string name,
        BridgeSnapshotWriter<T> writeSnapshot,
        PropertyDescriptor<T>[] properties,
        CheckedPropertyDescriptor<T>[] checkedProperties,
        CommandDescriptor<T>[] commands,
        string? contractFingerprint = null,
        WindowContentSession? content = null,
        BridgeInteractionDescriptor<T>[]? interactions = null,
        BridgeDataSubscriptionMember[]? dataSubscriptions = null,
        BridgeCollectionDescriptor<T>[]? collections = null)
        : this(transport, vm, name, null, writeSnapshot, properties, checkedProperties,
            commands, contractFingerprint, content, interactions, dataSubscriptions, collections)
    {
    }

    private ViewModelBridge(
        IBridgeTransport transport,
        T vm,
        string name,
        Action<Utf8JsonWriter, T, long>? writeSnapshot,
        BridgeSnapshotWriter<T>? writeSnapshotWithFields,
        PropertyDescriptor<T>[] properties,
        CheckedPropertyDescriptor<T>[] checkedProperties,
        CommandDescriptor<T>[] commands,
        string? contractFingerprint,
        WindowContentSession? content,
        BridgeInteractionDescriptor<T>[]? interactions,
        BridgeDataSubscriptionMember[]? dataSubscriptions,
        BridgeCollectionDescriptor<T>[]? collections)
    {
        _transport = transport;
        _vm = vm;
        _modelGate = BridgeModelGates.For(vm);
        _modelTurn = BridgeModelTurn.For(vm);
        _content = content;
        // A window can detach a content route and attach a new bridge to the
        // same route while a browser still holds the former state. Drawing
        // revisions from the window keeps that route's order monotonic.
        _revision = content?.NextRevision() ?? 0;
        _publishedRevision = _revision;
        _incrementalCollections = collections ?? [];
        _generatedCollectionNames = dataSubscriptions?.Where(member => member.EnumerateChildren is not null)
            .Select(member => member.PropertyName).ToHashSet(StringComparer.Ordinal) ?? [];
        _delivery = new(transport, name, _modelTurn);
        _name = name;
        _writeSnapshot = writeSnapshot ?? ((_, _, _) => throw new InvalidOperationException("A snapshot writer is required."));
        _writeSnapshotWithFields = writeSnapshotWithFields;
        _properties = properties;
        _checkedProperties = [];
        _commands = commands;
        _contractFingerprint = contractFingerprint;
        _errors = vm as INotifyDataErrorInfo;

        var bindings = new List<IDisposable>();
        var subscribedCommands = new List<ICommand>();
        try
        {
            if (content is not null && interactions is not null)
                foreach (var interaction in interactions) bindings.Add(interaction.Attach(content, vm, name));
            if (dataSubscriptions is { Length: > 0 })
                bindings.Add(new BridgeDataSubscriptions(vm, dataSubscriptions, Publish, TryPublishCollection, TryPublishItem,
                    rootObservedExternally: true, incrementalCollectionNames: _incrementalCollections.Select(descriptor => descriptor.Name).ToHashSet(StringComparer.Ordinal)));
            // Register the per-window observer before this Bridge subscribes
            // its snapshot publisher. A synchronous setter then advances the
            // field version before any emitted state can describe the value.
            if (checkedProperties.Length > 0)
            {
                if (content is null)
                {
                    // Preserve the long-standing direct Bridge path. It has
                    // no window owner, so it may use Set<Property> but cannot
                    // promise checked-write baselines or receipt retention.
                    foreach (var descriptor in checkedProperties)
                    {
                        var captured = descriptor;
                        bindings.Add(transport.Bind($"{name}Write{captured.Name}", _ =>
                            EncodeTerminal(new("failed", "Acknowledged field writes require a WindowContentSession."))));
                    }
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
                            snapshot: descriptor.Snapshot, equalityComparer: descriptor.Comparer,
                            canonicalize: value => EncodeCheckedValue(value, descriptor),
                            retainedValueByteCount: value => Encoding.UTF8.GetByteCount(EncodeCheckedValue(value, descriptor)));
                        bindings.Add(field.Lease);
                        checkedBindings.Add(new CheckedPropertyBinding(descriptor, field.Registry));
                    }
                    _checkedProperties = [.. checkedBindings];
                }
            }
            _vm.PropertyChanged += OnChanged;
            RefreshCollectionSubscriptions();
            if (_errors is not null) _errors.ErrorsChanged += OnErrorsChanged;
            foreach (var command in _commands)
            {
                var value = command.Get(_vm);
                if (value is ICommand nativeCommand)
                {
                    nativeCommand.CanExecuteChanged += OnCanExecuteChanged;
                    subscribedCommands.Add(nativeCommand);
                }
                if (command.Subscribe is { } subscribe) bindings.Add(subscribe(_vm, Publish));
            }

            bindings.Add(transport.Bind($"{name}Snapshot", _ => _modelTurn.Run(Reply)));
            foreach (var property in properties.Where(property => property.CanWrite))
            {
                var captured = property;
                bindings.Add(transport.Bind($"{name}Set{property.Name}", e => _modelTurn.Run(() => Set(captured, e))));
            }
            foreach (var property in _checkedProperties)
            {
                var captured = property;
                bindings.Add(transport.Bind($"{name}Write{captured.Descriptor.Name}", e => _modelTurn.Run(() => Write(captured, e))));
            }
            foreach (var command in commands)
            {
                var captured = command;
                bindings.Add(command.ExecuteAsync is null && command.ExecuteResultAsync is null && command.ExecuteStreamAsync is null
                    ? transport.Bind($"{name}{command.Name}", e => _modelTurn.Run(() => Execute(captured, e)))
                    : transport.BindAsync($"{name}{command.Name}", (e, token) => ExecuteAsync(captured, e, token)));
                // The familiar awaited command route stays the default. A
                // asynchronous descriptor gains an internal
                // admission route only when it is attached to a window-owned
                // content session with a generated contract fingerprint.
                if (content is not null && contractFingerprint is not null
                    && (captured.ExecuteAsync is not null || captured.ExecuteResultAsync is not null || captured.ExecuteStreamAsync is not null))
                    bindings.Add(transport.Bind($"{name}Start{captured.Name}",
                        e => _modelTurn.Run(() => StartOperation(content, captured, e))));
                bindings.Add(transport.Bind($"{name}Can{command.Name}", e => _modelTurn.Run(() => QueryAvailability(captured, e))));
            }
            _bindings = [.. bindings];
            _subscribedCommands = [.. subscribedCommands];
            RunicBridgeHotReload.Track(this);
        }
        catch
        {
            foreach (var binding in bindings) binding.Dispose();
            _vm.PropertyChanged -= OnChanged;
            foreach (var collection in _collections.Values) collection.CollectionChanged -= OnCollectionChanged;
            _collections.Clear();
            if (_errors is not null) _errors.ErrorsChanged -= OnErrorsChanged;
            foreach (var command in subscribedCommands) command.CanExecuteChanged -= OnCanExecuteChanged;
            throw;
        }
    }

    private string Reply()
    {
        lock (_modelGate) return IsInactive
            ? EncodeWithoutSnapshot(new("disconnected", "The Bridge is closed."))
            : Encode(protocol: true);
    }

    private string Set(PropertyDescriptor<T> property, IBridgeArguments e)
    {
        lock (_modelGate)
        {
            if (IsInactive) return EncodeWithoutSnapshot(new("disconnected", "The Bridge is closed."));
            try
            {
                property.Set(_vm, e);
                return EncodeTerminal();
            }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException)
            {
                return EncodeTerminal(new("rejected", $"{property.Name} has an invalid value."));
            }
            catch (Exception error)
            {
                Trace.TraceError($"Bridge setter {property.Name} failed: {error}");
                return EncodeTerminal(new("failed", $"Could not update {property.Name}."));
            }
        }
    }

    private string Write(CheckedPropertyBinding property, IBridgeArguments arguments)
    {
        lock (_modelGate)
        {
            if (IsInactive) return EncodeWithoutSnapshot(new("disconnected", "The Bridge is closed."));
            BridgeFieldWriteRequest<object?> request;
            try
            {
                var payload = arguments.GetString();
                if (payload.Length > MaximumFieldWritePayloadLength)
                    throw new FormatException("The checked write payload is too large.");
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new FormatException("Expected a field write object.");
                var requestId = root.GetProperty("requestId").GetString();
                if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > MaximumFieldWriteRequestIdLength)
                    throw new FormatException("A field write requestId is required.");
                if (!root.GetProperty("expectedVersion").TryGetInt64(out var expectedVersion)
                    || expectedVersion < 0)
                    throw new FormatException("A non-negative field version is required.");
                var expectedValue = ReadCheckedValue(root.GetProperty("expectedValue"), property.Descriptor);
                var value = ReadCheckedValue(root.GetProperty("value"), property.Descriptor);
                request = new BridgeFieldWriteRequest<object?>(requestId, expectedVersion, expectedValue, value);
            }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException or InvalidOperationException or KeyNotFoundException or OverflowException)
            {
                return EncodeTerminal(new("rejected", $"{property.Descriptor.Name} has an invalid checked write."));
            }

            try
            {
                var receipt = property.Registry.Apply(request);
                return EncodeFieldWrite(receipt, property);
            }
            catch (Exception error)
            {
                Trace.TraceError($"Bridge checked setter {property.Descriptor.Name} failed: {error}");
                return EncodeTerminal(new("failed", $"Could not update {property.Descriptor.Name}."));
            }
        }
    }

    private static object? ReadCheckedValue(JsonElement value, CheckedPropertyDescriptor<T> descriptor) => descriptor.ValueKind switch
    {
        CheckedFieldValueKind.String when value.ValueKind is JsonValueKind.String => value.GetString(),
        CheckedFieldValueKind.NullableString when value.ValueKind is JsonValueKind.String => value.GetString(),
        CheckedFieldValueKind.NullableString when value.ValueKind is JsonValueKind.Null => null,
        CheckedFieldValueKind.Int32 when value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var integer) => integer,
        CheckedFieldValueKind.Boolean when value.ValueKind is JsonValueKind.True => true,
        CheckedFieldValueKind.Boolean when value.ValueKind is JsonValueKind.False => false,
        CheckedFieldValueKind.Json => descriptor.Read!(value),
        _ => throw new FormatException("The checked field value does not match its generated type."),
    };

    private static string EncodeCheckedValue(object? value, CheckedPropertyDescriptor<T> descriptor) =>
        BridgeWire.EncodeCanonical(writer => WriteCheckedValue(writer, value, descriptor));

    private static void WriteCheckedValue(Utf8JsonWriter writer, object? value, CheckedPropertyDescriptor<T> descriptor)
    {
        switch (descriptor.ValueKind)
        {
            case CheckedFieldValueKind.String:
                writer.WriteStringValue((string)value!);
                return;
            case CheckedFieldValueKind.NullableString:
                if (value is null) writer.WriteNullValue();
                else writer.WriteStringValue((string)value);
                return;
            case CheckedFieldValueKind.Int32:
                writer.WriteNumberValue((int)value!);
                return;
            case CheckedFieldValueKind.Boolean:
                writer.WriteBooleanValue((bool)value!);
                return;
            case CheckedFieldValueKind.Json:
                descriptor.Write!(writer, value);
                return;
            default:
                throw new InvalidOperationException("The checked field type is unsupported.");
        }
    }

    private string Execute(CommandDescriptor<T> descriptor, IBridgeArguments arguments)
    {
        lock (_modelGate)
        {
            if (IsInactive) return EncodeWithoutSnapshot(new("disconnected", "The Bridge is closed."));
            try
            {
                var argument = descriptor.ReadArgument?.Invoke(arguments);
                var command = descriptor.Get(_vm);
                if (!IsAvailable(descriptor, argument)) return EncodeTerminal(new("rejected", $"{descriptor.Name} is unavailable."));
                using var invocation = EnterInvocation(descriptor, arguments, CancellationToken.None);
                ((ICommand)command).Execute(argument);
                return EncodeTerminal();
            }
            catch (OperationCanceledException)
            {
                return EncodeTerminal(new("cancelled", $"{descriptor.Name} was cancelled."));
            }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException)
            {
                return EncodeTerminal(new("rejected", $"{descriptor.Name} has an invalid argument."));
            }
            catch (Exception error)
            {
                Trace.TraceError($"Bridge command {descriptor.Name} failed: {error}");
                return EncodeTerminal(new("failed", $"{descriptor.Name} failed."));
            }
        }
    }

    private async ValueTask<string> ExecuteAsync(CommandDescriptor<T> descriptor, IBridgeArguments arguments, CancellationToken token)
    {
        // The awaited route is window work like an admitted operation: closing
        // the window waits for it before the host disposes its scope, and
        // cancels it after the close timeout.
        using var admission = _content?.Operations.TryBeginAwaited(token);
        if (_content is not null && admission is null)
            return EncodeWithoutSnapshot(new("disconnected", "The window is closing."));
        var cancellation = admission?.Token ?? token;
        Task<BridgeOperationResult>? execution = null;
        try
        {
            var rejection = _modelTurn.Run(() =>
            {
                if (IsInactive) return EncodeWithoutSnapshot(new("disconnected", "The Bridge is closed."));
                var argument = descriptor.ReadArgument?.Invoke(arguments);
                if (!IsAvailable(descriptor, argument))
                    return EncodeTerminal(new("rejected", $"{descriptor.Name} is unavailable."));
                using var invocation = EnterInvocation(descriptor, arguments, cancellation);
                execution = InvokeCommandAsync(descriptor, argument, cancellation);
                return null;
            });
            if (rejection is not null) return rejection;
            await execution!.ConfigureAwait(false);
            return ReplyAfterCommand(() => EncodeTerminal());
        }
        catch (OperationCanceledException)
        { return ReplyAfterCommand(() => EncodeTerminal(new("cancelled", $"{descriptor.Name} was cancelled."))); }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException)
        { return ReplyAfterCommand(() => EncodeTerminal(new("rejected", $"{descriptor.Name} has an invalid argument."))); }
        catch (Exception error)
        {
            Trace.TraceError($"Bridge command {descriptor.Name} failed: {error}");
            return ReplyAfterCommand(() => EncodeTerminal(new("failed", $"{descriptor.Name} failed.")));
        }
    }

    // The model context can be gone when an awaited command completes after
    // its window was finalized. There is no state left to capture.
    private string ReplyAfterCommand(Func<string> encode)
    {
        try { return _modelTurn.Run(encode); }
        catch (ObjectDisposedException)
        { return EncodeWithoutSnapshot(new("disconnected", "The window closed before the command replied.")); }
    }

    private IDisposable? EnterInvocation(CommandDescriptor<T> descriptor, IBridgeArguments arguments, CancellationToken token) =>
        _content is null ? null : RunicInteractionInvocation.Enter(_content, _name, arguments, token, descriptor.Name);

    private bool IsAvailable(CommandDescriptor<T> descriptor, object? argument) =>
        descriptor.CanExecute?.Invoke(_vm, argument)
        ?? (descriptor.Get(_vm) is ICommand command && command.CanExecute(argument));

    private string QueryAvailability(CommandDescriptor<T> descriptor, IBridgeArguments arguments)
    {
        lock (_modelGate)
        {
            if (IsInactive) return "false";
            try { return IsAvailable(descriptor, descriptor.ReadArgument?.Invoke(arguments)) ? "true" : "false"; }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException or InvalidOperationException)
            { return "false"; }
        }
    }

    private async Task<BridgeOperationResult> InvokeCommandAsync(CommandDescriptor<T> descriptor, object? argument, CancellationToken token)
    {
        if (descriptor.ExecuteStreamAsync is { } executeStream)
            return await executeStream(_vm, new BridgeOperationExecution(descriptor.CreateStream!()), token, argument).ConfigureAwait(false);
        if (descriptor.ExecuteResultAsync is { } executeResult)
            return await executeResult(_vm, token, argument).ConfigureAwait(false);
        await descriptor.ExecuteAsync!(_vm, token, argument).ConfigureAwait(false);
        return BridgeOperationResult.Empty;
    }

    private string StartOperation(WindowContentSession content, CommandDescriptor<T> descriptor, IBridgeArguments arguments)
    {
        lock (_modelGate)
        {
            if (IsInactive) return EncodeOperationStartFailure("disconnected", "The Bridge is closed.");
            try
            {
                string requestId;
                object? argument = null;
                if (descriptor.ReadArgument is null) requestId = arguments.GetString();
                else
                {
                    var payload = arguments.GetString();
                    if (payload.Length > MaximumFieldWritePayloadLength) throw new FormatException("The command input is too large.");
                    using var document = JsonDocument.Parse(payload);
                    requestId = document.RootElement.GetProperty("requestId").GetString() ?? "";
                    argument = descriptor.ReadArgument(new JsonBridgeArguments(document.RootElement.GetProperty("input").GetRawText(), arguments));
                }
                _ = BridgeOperationIdentity.Create(OperationContract(), requestId);
                var canonicalInput = descriptor.EncodeArgument?.Invoke(argument) ?? WriteJson(writer =>
                {
                    switch (argument)
                    {
                        case null: writer.WriteNullValue(); break;
                        case string value: writer.WriteStringValue(value); break;
                        case int value: writer.WriteNumberValue(value); break;
                        case bool value: writer.WriteBooleanValue(value); break;
                        default: throw new FormatException("The command needs a generated input codec.");
                    }
                });
                var request = new BridgeOperationRequest(OperationContract(), descriptor.Name, requestId, BridgeOperationRequest.CanonicalDigest(canonicalInput));
                var admission = descriptor.ExecuteStreamAsync is { } executeStream
                    ? content.Operations.Accept(request, () => IsAvailable(descriptor, argument), descriptor.CreateStream!(),
                        (execution, cancellation) =>
                        {
                            using var invocation = EnterInvocation(descriptor, arguments, cancellation);
                            return executeStream(_vm, execution, cancellation, argument);
                        })
                    : content.Operations.Accept(request,
                    () => IsAvailable(descriptor, argument),
                    cancellation =>
                    {
                        using var invocation = EnterInvocation(descriptor, arguments, cancellation);
                        return InvokeCommandAsync(descriptor, argument, cancellation);
                    });
                return BridgeOperationRouter.EncodeAdmission(admission);
            }
            catch (OperationCanceledException)
            { return EncodeOperationStartFailure("cancelled", $"{descriptor.Name} was cancelled."); }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException or InvalidOperationException)
            { return EncodeOperationStartFailure("rejected", $"{descriptor.Name} has an invalid argument."); }
            catch (Exception error)
            {
                Trace.TraceError($"Bridge operation admission {descriptor.Name} failed: {error}");
                return EncodeOperationStartFailure("failed", $"{descriptor.Name} could not start.");
            }
        }
    }

    private sealed class JsonBridgeArguments(string json, IBridgeArguments source) : IBridgeArguments
    {
        public string GetString() => json;
        public long GetInt64() { using var document = JsonDocument.Parse(json); return document.RootElement.GetInt64(); }
        public bool GetBoolean() { using var document = JsonDocument.Parse(json); return document.RootElement.GetBoolean(); }
        public string? ClientKey => source.ClientKey;
        public string? ConnectionKey => source.ConnectionKey;
    }

    // A ViewModel type and generated fingerprint describe its wire shape, but
    // a window can host several instances of that shape. The route/reference
    // separates their advanced request-id namespaces.
    private string OperationContract() => $"{typeof(T).FullName}:{_contractFingerprint}:{_name}";

    private static string EncodeOperationStartFailure(string kind, string reason) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("kind", kind);
        writer.WriteString("reason", reason);
        writer.WriteEndObject();
    });

    private string EncodeTerminal(BridgeFailure? error = null) =>
        IsInactive ? EncodeWithoutSnapshot(error) : Encode(error);

    private string EncodeWithoutSnapshot(BridgeFailure? error = null) =>
        Encode(error, includeSnapshot: false);

    private string Encode(BridgeFailure? error = null, bool includeSnapshot = true, bool protocol = false)
    {
        if (!includeSnapshot) return EncodeReply(error, snapshot: null);
        try
        {
            var snapshot = WriteSnapshot();
            return IsInactive ? EncodeWithoutSnapshot(error) : EncodeReply(error, snapshot, protocol);
        }
        catch (BridgeSnapshotDetachedException)
        {
            return EncodeWithoutSnapshot(error);
        }
    }

    private string WriteSnapshot()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        using (BridgeSnapshotPublication.Enter(() => IsInactive))
            if (_writeSnapshotWithFields is { } writerWithFields)
                writerWithFields(writer, _vm, _revision, WriteFieldMetadata);
            else _writeSnapshot(writer, _vm, _revision);
        return Encoding.UTF8.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private void WriteFieldMetadata(Utf8JsonWriter writer)
    {
        if (_checkedProperties.Length == 0) return;
        writer.WritePropertyName("__runicFields");
        writer.WriteStartObject();
        foreach (var property in _checkedProperties)
        {
            writer.WritePropertyName(property.Descriptor.WireName);
            writer.WriteStartObject();
            writer.WriteNumber("version", property.Registry.Current.Version);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private string EncodeFieldWrite(BridgeFieldWriteReceipt<object?> receipt, CheckedPropertyBinding property)
    {
        try
        {
            var snapshot = WriteSnapshot();
            return IsInactive ? EncodeWithoutSnapshot(new("disconnected", "The Bridge is closed.")) : WriteJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", true);
                writer.WritePropertyName("state");
                using (var document = JsonDocument.Parse(snapshot)) document.RootElement.WriteTo(writer);
                writer.WriteNull("error");
                writer.WritePropertyName("receipt");
                writer.WriteStartObject();
                switch (receipt.Kind)
                {
                    case BridgeFieldWriteReceiptKind.Applied:
                        writer.WriteString("kind", "applied");
                        WriteFieldSnapshot(writer, "snapshot", receipt.Current, property.Descriptor);
                        if (receipt.Validation is not null) writer.WriteString("validation", receipt.Validation);
                        break;
                    case BridgeFieldWriteReceiptKind.PostApplyValidationFailed:
                        writer.WriteString("kind", "committed-with-error");
                        WriteFieldSnapshot(writer, "snapshot", receipt.Current, property.Descriptor);
                        writer.WriteString("message", receipt.Message ?? "The field changed but its validation did not complete.");
                        break;
                    case BridgeFieldWriteReceiptKind.Conflict:
                        writer.WriteString("kind", "conflict");
                        WriteFieldSnapshot(writer, "incoming", receipt.Current, property.Descriptor);
                        writer.WriteString("message", receipt.Message ?? "The field baseline no longer matches.");
                        break;
                    default:
                        writer.WriteString("kind", "rejected");
                        writer.WriteString("message", receipt.Message ?? "The field write was rejected.");
                        break;
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
        }
        catch (BridgeSnapshotDetachedException)
        {
            return EncodeWithoutSnapshot(new("disconnected", "The Bridge is closed."));
        }
    }

    private static void WriteFieldSnapshot(Utf8JsonWriter writer, string name, BridgeFieldSnapshot<object?> snapshot,
        CheckedPropertyDescriptor<T> descriptor)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WritePropertyName("value");
        WriteCheckedValue(writer, snapshot.Value, descriptor);
        writer.WriteNumber("version", snapshot.Version);
        writer.WriteEndObject();
    }

    // The snapshot route, which a client reads first, states the wire protocol
    // version. Clients ignore envelope members they do not know.
    private static string EncodeReply(BridgeFailure? error, string? snapshot, bool protocol = false) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteBoolean("ok", error is null);
        writer.WritePropertyName("state");
        if (snapshot is null) writer.WriteNullValue();
        else
        {
            using var document = JsonDocument.Parse(snapshot);
            document.RootElement.WriteTo(writer);
        }
        if (error is null) writer.WriteNull("error");
        else
        {
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("kind", error.Kind);
            writer.WriteString("message", error.Message);
            writer.WriteEndObject();
        }
        if (protocol) writer.WriteNumber("protocol", BridgeProtocol.Version);
        writer.WriteEndObject();
    });

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => _modelTurn.Run(() => OnChangedCore(sender, e));

    private void OnChangedCore(object? sender, PropertyChangedEventArgs e)
    {
        lock (_modelGate)
        {
            if (IsInactive) return;
            RefreshCollectionSubscriptions();
            Publish();
        }
    }
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is not INotifyCollectionChanged collection || !TryPublishCollection(collection, e)) Publish();
    }
    private void OnErrorsChanged(object? sender, DataErrorsChangedEventArgs e) => Publish();
    private void OnCanExecuteChanged(object? sender, EventArgs e) => Publish();

    private void Publish()
    {
        _modelTurn.Run(PublishCore);
    }

    void IBridgeSnapshotBatchParticipant.FlushSnapshotBatch() => _modelTurn.Run(PublishBatchedSnapshotCore);

    private void PublishCore()
    {
        lock (_modelGate)
        {
            if (IsInactive) return;
            _revision = NextRevision();
            _requiresSnapshot = true;
            // Capture is more expensive than queue delivery. A batch defers
            // only capture, never the revision: route replies that serialize
            // state during the batch must remain newer than their predecessor.
            if (BridgeSnapshotBatch.TryDefer(_vm, this)) return;
            PublishSnapshotCore();
        }
    }

    private long NextRevision() => _content?.NextRevision() ?? _revision + 1;

    private void PublishBatchedSnapshotCore()
    {
        lock (_modelGate)
        {
            if (IsInactive) return;
            // Each notification already advanced the revision in PublishCore.
            // The outer batch only serializes the final revision once.
            PublishCollectionOrSnapshotCore();
        }
    }

    private void PublishSnapshotCore()
    {
        try
        {
            var state = WriteSnapshot();
            if (!IsInactive) _delivery.Enqueue(state);
            _publishedRevision = _revision;
            _requiresSnapshot = false;
            _pendingCollectionChanges.Clear();
        }
        catch (BridgeSnapshotDetachedException)
        {
            // The session detached this route after this callback started.
            // There is no current endpoint to publish to.
        }
    }

    private bool TryPublishCollection(INotifyCollectionChanged collection, NotifyCollectionChangedEventArgs args)
    {
        return _modelTurn.Run(() =>
        {
            lock (_modelGate)
            {
                if (IsInactive) return true;
                var descriptors = _incrementalCollections.Where(descriptor => ReferenceEquals(descriptor.Get(_vm), collection)).ToArray();
                if (descriptors.Length == 0) return false;
                foreach (var descriptor in descriptors) _collectionIndexes.Remove(descriptor);
                _revision = NextRevision();
                var kind = args.Action switch
                {
                    NotifyCollectionChangedAction.Add => "add",
                    NotifyCollectionChangedAction.Remove => "remove",
                    NotifyCollectionChangedAction.Replace => "replace",
                    NotifyCollectionChangedAction.Move => "move",
                    _ => "reset"
                };
                if (kind == "reset" || args.NewStartingIndex < 0 && kind is "add" or "replace" or "move" ||
                    args.OldStartingIndex < 0 && kind is "remove" or "replace" or "move") _requiresSnapshot = true;
                if (!_requiresSnapshot)
                    foreach (var descriptor in descriptors)
                    {
                        var keys = (kind == "add" ? args.NewItems : args.OldItems)?.Cast<object?>().Select(descriptor.Key).ToArray() ?? [];
                        var items = kind is "add" or "replace"
                            ? args.NewItems?.Cast<object?>().Select(item => WriteJson(writer => descriptor.WriteItem(writer, item))).ToArray() ?? []
                            : [];
                        _pendingCollectionChanges.Add(new(descriptor, kind,
                            kind == "remove" ? args.OldStartingIndex : args.NewStartingIndex,
                            args.OldStartingIndex, keys, items));
                        if (_pendingCollectionChanges.Count > 4096) { _requiresSnapshot = true; break; }
                    }
                if (!BridgeSnapshotBatch.TryDefer(_vm, this)) PublishCollectionOrSnapshotCore();
                return true;
            }
        });
    }

    private void PublishCollectionOrSnapshotCore()
    {
        if (_requiresSnapshot || _pendingCollectionChanges.Count == 0) { PublishSnapshotCore(); return; }
        var delta = WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("__runicDelta", 1);
            writer.WriteNumber("baseRevision", _publishedRevision);
            writer.WriteNumber("revision", _revision);
            writer.WriteStartArray("changes");
            foreach (var change in _pendingCollectionChanges)
            {
                writer.WriteStartObject();
                writer.WriteString("field", change.Descriptor.Name);
                writer.WriteString("kind", change.Kind);
                writer.WriteNumber("index", change.Index);
                writer.WriteNumber("oldIndex", change.OldIndex);
                writer.WriteStartArray("keys");
                foreach (var key in change.Keys) writer.WriteStringValue(key);
                writer.WriteEndArray();
                writer.WriteStartArray("items");
                foreach (var item in change.Items) writer.WriteRawValue(item);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        if (!_delivery.EnqueueDelta(delta)) { PublishSnapshotCore(); return; }
        _publishedRevision = _revision;
        _pendingCollectionChanges.Clear();
    }

    private bool TryPublishItem(object item)
    {
        return _modelTurn.Run(() =>
        {
            lock (_modelGate)
            {
                if (IsInactive) return true;
                var handled = false;
                foreach (var descriptor in _incrementalCollections)
                {
                    if (descriptor.Get(_vm) is not System.Collections.IList rows) continue;
                    if (!_collectionIndexes.TryGetValue(descriptor, out var indexes))
                    {
                        indexes = new(ReferenceEqualityComparer.Instance);
                        for (var index = 0; index < rows.Count; index++)
                            if (rows[index] is { } row) indexes[row] = index;
                        _collectionIndexes[descriptor] = indexes;
                    }
                    if (!indexes.TryGetValue(item, out var at) || at >= rows.Count || !ReferenceEquals(rows[at], item)) continue;
                    if (!handled) _revision = NextRevision();
                    handled = true;
                    if (!_requiresSnapshot)
                        _pendingCollectionChanges.Add(new(descriptor, "replace", at, at,
                            [descriptor.Key(item)], [WriteJson(writer => descriptor.WriteItem(writer, item))]));
                    if (_pendingCollectionChanges.Count > 4096) _requiresSnapshot = true;
                }
                if (handled && !BridgeSnapshotBatch.TryDefer(_vm, this)) PublishCollectionOrSnapshotCore();
                return handled;
            }
        });
    }

    Type IHotReloadableBridge.ContractModelType => typeof(T);

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

    void IHotReloadableBridge.RefreshAfterHotReload() => Publish();

    private void RefreshCollectionSubscriptions()
    {
        _collectionIndexes.Clear();
        foreach (var property in _properties)
        {
            if (_generatedCollectionNames.Contains(property.Name)) continue;
            var next = property.Get(_vm) as INotifyCollectionChanged;
            _collections.TryGetValue(property.Name, out var previous);
            if (ReferenceEquals(previous, next)) continue;
            if (previous is not null) previous.CollectionChanged -= OnCollectionChanged;
            if (next is not null)
            {
                next.CollectionChanged += OnCollectionChanged;
                _collections[property.Name] = next;
            }
            else _collections.Remove(property.Name);
        }
    }

    /// <summary>Unbinds the bridge's routes and stops observing the ViewModel.</summary>
    public virtual void Dispose()
    {
        _modelTurn.RunForTeardown(DisposeCore);
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        lock (_modelGate)
        {
            if (_disposed) return;
            _disposed = true;
            _delivery.Dispose();
            _vm.PropertyChanged -= OnChanged;
            foreach (var collection in _collections.Values) collection.CollectionChanged -= OnCollectionChanged;
            _collections.Clear();
            if (_errors is not null) _errors.ErrorsChanged -= OnErrorsChanged;
            foreach (var command in _subscribedCommands)
                command.CanExecuteChanged -= OnCanExecuteChanged;
            foreach (var binding in _bindings) binding.Dispose();
        }
    }

    void IBridgeDetachmentSignal.BeginDetaching() => Interlocked.Exchange(ref _detaching, 1);

    private bool IsInactive => _disposed || Volatile.Read(ref _detaching) != 0;

    private sealed record CheckedPropertyBinding(CheckedPropertyDescriptor<T> Descriptor,
        BridgeFieldWriteRegistry<object?> Registry);
}

/// <summary>JSON helpers called by generated bridges.</summary>
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
            foreach (var error in errors)
                writer.WriteStringValue(error is ValidationResult result
                    ? result.ErrorMessage ?? "Invalid value."
                    : error?.ToString() ?? "Invalid value.");
        }
        writer.WriteEndArray();
    }
}
