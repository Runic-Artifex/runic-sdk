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
public sealed class PropertyDescriptor<T>(string name, Func<T, object?> getter, Action<T, IBridgeArguments>? setter)
{
    public string Name { get; } = name;
    public bool CanWrite => setter is not null;
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
public enum CheckedFieldValueKind { String, NullableString, Int32, Boolean }

public sealed class CheckedPropertyDescriptor<T>(
    string name,
    string wireName,
    CheckedFieldValueKind valueKind,
    Func<T, object?> getter,
    Action<T, object?> setter)
{
    public string Name { get; } = name;
    public string WireName { get; } = wireName;
    public CheckedFieldValueKind ValueKind { get; } = valueKind;
    internal Func<T, object?> Get { get; } = getter;
    internal Action<T, object?> Set { get; } = setter;
}

// Generated snapshot writers call the supplied callback while their JSON
// object is still open. The base Bridge uses it for hidden per-field versions;
// generated public State interfaces never expose those versions.
public delegate void BridgeSnapshotWriter<T>(Utf8JsonWriter writer, T viewModel,
    long revision, Action<Utf8JsonWriter> writeFieldMetadata);

public sealed record CommandDescriptor<T>(
    string Name,
    Func<T, ICommand> Get,
    Func<T, CancellationToken, object?, Task>? ExecuteAsync = null,
    Func<IBridgeArguments, object?>? ReadArgument = null);

internal sealed record BridgeFailure(string Kind, string Message);

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

public class ViewModelBridge<T> : IDisposable, IHotReloadableBridge, IBridgeDetachmentSignal where T : INotifyPropertyChanged
{
    private const int MaximumFieldWritePayloadLength = 64 * 1024;
    private const int MaximumFieldWriteRequestIdLength = 256;
    private readonly object _modelGate;
    private readonly IBridgeTransport _transport;
    private readonly T _vm;
    private readonly string _name;
    private readonly Action<Utf8JsonWriter, T, long> _writeSnapshot;
    private readonly BridgeSnapshotWriter<T>? _writeSnapshotWithFields;
    private readonly PropertyDescriptor<T>[] _properties;
    private readonly CheckedPropertyBinding[] _checkedProperties;
    private readonly CommandDescriptor<T>[] _commands;
    private readonly string? _contractFingerprint;
    private readonly IDisposable[] _bindings;
    private readonly INotifyDataErrorInfo? _errors;
    private readonly Dictionary<string, INotifyCollectionChanged> _collections = new();
    private long _revision;
    private bool _disposed;
    private int _detaching;

    protected ViewModelBridge(
        IBridgeTransport transport,
        T vm,
        string name,
        Action<Utf8JsonWriter, T, long> writeSnapshot,
        PropertyDescriptor<T>[] properties,
        CommandDescriptor<T>[] commands,
        string? contractFingerprint = null,
        WindowContentSession? content = null)
        : this(transport, vm, name, writeSnapshot, null, properties, [], commands,
            contractFingerprint, content)
    {
    }

    protected ViewModelBridge(
        IBridgeTransport transport,
        T vm,
        string name,
        BridgeSnapshotWriter<T> writeSnapshot,
        PropertyDescriptor<T>[] properties,
        CheckedPropertyDescriptor<T>[] checkedProperties,
        CommandDescriptor<T>[] commands,
        string? contractFingerprint = null,
        WindowContentSession? content = null)
        : this(transport, vm, name, null, writeSnapshot, properties, checkedProperties,
            commands, contractFingerprint, content)
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
        WindowContentSession? content)
    {
        _transport = transport;
        _vm = vm;
        _modelGate = BridgeModelGates.For(vm);
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
                    _checkedProperties = checkedProperties.Select(descriptor =>
                        new CheckedPropertyBinding(descriptor, content.FieldWrites.GetOrCreate(
                            _vm, contract, descriptor.Name,
                            () => descriptor.Get(_vm), value => descriptor.Set(_vm, value)))).ToArray();
                }
            }
            _vm.PropertyChanged += OnChanged;
            RefreshCollectionSubscriptions();
            if (_errors is not null) _errors.ErrorsChanged += OnErrorsChanged;
            foreach (var command in _commands)
            {
                var value = command.Get(_vm);
                value.CanExecuteChanged += OnCanExecuteChanged;
                subscribedCommands.Add(value);
            }

            bindings.Add(transport.Bind($"{name}Snapshot", _ => Reply()));
            foreach (var property in properties.Where(property => property.CanWrite))
            {
                var captured = property;
                bindings.Add(transport.Bind($"{name}Set{property.Name}", e => Set(captured, e)));
            }
            foreach (var property in _checkedProperties)
            {
                var captured = property;
                bindings.Add(transport.Bind($"{name}Write{captured.Descriptor.Name}", e => Write(captured, e)));
            }
            foreach (var command in commands)
            {
                var captured = command;
                bindings.Add(command.ExecuteAsync is null
                    ? transport.Bind($"{name}{command.Name}", e => Execute(captured, e))
                    : transport.BindAsync($"{name}{command.Name}", (e, token) => ExecuteAsync(captured, e, token)));
                // The familiar awaited command route stays the default. A
                // zero-argument asynchronous descriptor gains an internal
                // admission route only when it is attached to a window-owned
                // content session with a generated contract fingerprint.
                if (content is not null && contractFingerprint is not null
                    && captured.ExecuteAsync is not null && captured.ReadArgument is null)
                    bindings.Add(transport.Bind($"{name}Start{captured.Name}",
                        e => StartOperation(content, captured, e)));
            }
            _bindings = [.. bindings];
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
            : Encode();
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
                var expectedValue = ReadCheckedValue(root.GetProperty("expectedValue"), property.Descriptor.ValueKind);
                var value = ReadCheckedValue(root.GetProperty("value"), property.Descriptor.ValueKind);
                request = new BridgeFieldWriteRequest<object?>(requestId, expectedVersion, expectedValue, value);
            }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException or InvalidOperationException)
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

    private static object? ReadCheckedValue(JsonElement value, CheckedFieldValueKind valueKind) => valueKind switch
    {
        CheckedFieldValueKind.String when value.ValueKind is JsonValueKind.String => value.GetString(),
        CheckedFieldValueKind.NullableString when value.ValueKind is JsonValueKind.String => value.GetString(),
        CheckedFieldValueKind.NullableString when value.ValueKind is JsonValueKind.Null => null,
        CheckedFieldValueKind.Int32 when value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var integer) => integer,
        CheckedFieldValueKind.Boolean when value.ValueKind is JsonValueKind.True => true,
        CheckedFieldValueKind.Boolean when value.ValueKind is JsonValueKind.False => false,
        _ => throw new FormatException("The checked field value does not match its generated type."),
    };

    private static void WriteCheckedValue(Utf8JsonWriter writer, object? value, CheckedFieldValueKind valueKind)
    {
        switch (valueKind)
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
                if (!command.CanExecute(argument)) return EncodeTerminal(new("rejected", $"{descriptor.Name} is unavailable."));
                command.Execute(argument);
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
        object? argument;
        lock (_modelGate)
        {
            if (IsInactive) return EncodeWithoutSnapshot(new("disconnected", "The Bridge is closed."));
            try
            {
                argument = descriptor.ReadArgument?.Invoke(arguments);
                if (!descriptor.Get(_vm).CanExecute(argument))
                    return EncodeTerminal(new("rejected", $"{descriptor.Name} is unavailable."));
            }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException)
            {
                return EncodeTerminal(new("rejected", $"{descriptor.Name} has an invalid argument."));
            }
        }
        try
        {
            await descriptor.ExecuteAsync!(_vm, token, argument).ConfigureAwait(false);
            // A request to cancel does not change a command that completed
            // successfully into a cancelled outcome, even if the request was
            // made just before the task returned.
            lock (_modelGate) return EncodeTerminal();
        }
        catch (OperationCanceledException)
        {
            lock (_modelGate) return EncodeTerminal(new("cancelled", $"{descriptor.Name} was cancelled."));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException)
        {
            lock (_modelGate) return EncodeTerminal(new("rejected", $"{descriptor.Name} has an invalid argument."));
        }
        catch (Exception error)
        {
            Trace.TraceError($"Bridge command {descriptor.Name} failed: {error}");
            lock (_modelGate) return EncodeTerminal(new("failed", $"{descriptor.Name} failed."));
        }
    }

    private string StartOperation(WindowContentSession content, CommandDescriptor<T> descriptor, IBridgeArguments arguments)
    {
        lock (_modelGate)
        {
            if (IsInactive) return EncodeOperationStartFailure("disconnected", "The Bridge is closed.");

            string requestId;
            try
            {
                requestId = arguments.GetString();
                // Validate before command admission so malformed external
                // input never reaches a window operation registry.
                _ = BridgeOperationIdentity.Create(OperationContract(), requestId);
            }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException or InvalidOperationException)
            {
                return EncodeOperationStartFailure("invalid-request", "The operation request is invalid.");
            }

            try
            {
                var command = descriptor.Get(_vm);
                if (!command.CanExecute(null))
                    return EncodeOperationStartFailure("rejected", $"{descriptor.Name} is unavailable.");

                // Registry admission records the request before this delegate
                // runs. Its synchronous prefix executes in the model turn,
                // allowing the MVVM command to capture state before async I/O.
                var admission = content.Operations.Accept(OperationContract(), requestId,
                    cancellation => descriptor.ExecuteAsync!(_vm, cancellation, null));
                return BridgeOperationRouter.EncodeAdmission(admission);
            }
            catch (OperationCanceledException)
            {
                return EncodeOperationStartFailure("cancelled", $"{descriptor.Name} was cancelled.");
            }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException)
            {
                return EncodeOperationStartFailure("rejected", $"{descriptor.Name} has an invalid argument.");
            }
            catch (Exception error)
            {
                Trace.TraceError($"Bridge operation admission {descriptor.Name} failed: {error}");
                return EncodeOperationStartFailure("failed", $"{descriptor.Name} could not start.");
            }
        }
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

    private string Encode(BridgeFailure? error = null, bool includeSnapshot = true)
    {
        if (!includeSnapshot) return EncodeReply(error, snapshot: null);
        try
        {
            var snapshot = WriteSnapshot();
            return IsInactive ? EncodeWithoutSnapshot(error) : EncodeReply(error, snapshot);
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
                        WriteFieldSnapshot(writer, "snapshot", receipt.Current, property.Descriptor.ValueKind);
                        if (receipt.Validation is not null) writer.WriteString("validation", receipt.Validation);
                        break;
                    case BridgeFieldWriteReceiptKind.PostApplyValidationFailed:
                        writer.WriteString("kind", "committed-with-error");
                        WriteFieldSnapshot(writer, "snapshot", receipt.Current, property.Descriptor.ValueKind);
                        writer.WriteString("message", receipt.Message ?? "The field changed but its validation did not complete.");
                        break;
                    case BridgeFieldWriteReceiptKind.Conflict:
                        writer.WriteString("kind", "conflict");
                        WriteFieldSnapshot(writer, "incoming", receipt.Current, property.Descriptor.ValueKind);
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
        CheckedFieldValueKind valueKind)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WritePropertyName("value");
        WriteCheckedValue(writer, snapshot.Value, valueKind);
        writer.WriteNumber("version", snapshot.Version);
        writer.WriteEndObject();
    }

    private string EncodeReply(BridgeFailure? error, string? snapshot) => WriteJson(writer =>
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
        writer.WriteEndObject();
    });

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        lock (_modelGate)
        {
            if (IsInactive) return;
            RefreshCollectionSubscriptions();
            Publish();
        }
    }
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Publish();
    private void OnErrorsChanged(object? sender, DataErrorsChangedEventArgs e) => Publish();
    private void OnCanExecuteChanged(object? sender, EventArgs e) => Publish();

    private void Publish()
    {
        lock (_modelGate)
        {
            if (IsInactive) return;
            _revision++;
            try
            {
                var state = WriteSnapshot();
                if (!IsInactive) _transport.Publish(_name, state);
            }
            catch (BridgeSnapshotDetachedException)
            {
                // The session detached this route after this callback started.
                // There is no current endpoint to publish to.
            }
        }
    }

    Type IHotReloadableBridge.ContractModelType => typeof(T);

    string? IHotReloadableBridge.ContractMismatch()
    {
#if DEBUG
        if (_contractFingerprint is not null &&
            !string.Equals(_contractFingerprint, BridgeContractShape.Compute(typeof(T)), StringComparison.Ordinal))
            return $"{typeof(T).FullName} changed its generated Bridge contract. Rebuild and restart the .NET app.";
#endif
        return null;
    }

    void IHotReloadableBridge.RefreshAfterHotReload() => Publish();

    private void RefreshCollectionSubscriptions()
    {
        foreach (var property in _properties)
        {
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

    public virtual void Dispose()
    {
        lock (_modelGate)
        {
            if (_disposed) return;
            _disposed = true;
            _vm.PropertyChanged -= OnChanged;
            foreach (var collection in _collections.Values) collection.CollectionChanged -= OnCollectionChanged;
            _collections.Clear();
            if (_errors is not null) _errors.ErrorsChanged -= OnErrorsChanged;
            foreach (var command in _commands) command.Get(_vm).CanExecuteChanged -= OnCanExecuteChanged;
            foreach (var binding in _bindings) binding.Dispose();
        }
    }

    void IBridgeDetachmentSignal.BeginDetaching() => Interlocked.Exchange(ref _detaching, 1);

    private bool IsInactive => _disposed || Volatile.Read(ref _detaching) != 0;

    private sealed record CheckedPropertyBinding(CheckedPropertyDescriptor<T> Descriptor,
        BridgeFieldWriteRegistry<object?> Registry);
}

public static class BridgeJson
{
    public static string ReadRequiredString(string json) =>
        ReadNullableString(json) ?? throw new FormatException("Expected a string.");

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
