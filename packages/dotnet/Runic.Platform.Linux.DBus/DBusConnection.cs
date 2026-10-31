using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Tmds.DBus.Protocol;
using Wire = Tmds.DBus.Protocol.DBusConnection;

namespace Runic.Platform.Linux.DBus;

/// <summary>A connection to a message bus or a D-Bus peer.</summary>
/// <remarks>
/// <para>
/// Calls, signals and exported objects all use this connection. Its methods are safe to call from any thread; their
/// continuations and every signal run on the thread pool, never on the caller's synchronization context, so a
/// ViewModel moves results to its model context itself. A method call throws <see cref="DBusException"/> for an error
/// reply, or with <see cref="DBusException.Disconnected"/> once the connection has closed.
/// </para>
/// <para>
/// Applications usually take <see cref="IDBusConnections"/> from dependency injection, which shares one connection per
/// bus for the application. A connection created with <see cref="ConnectAsync(DBusBus, CancellationToken)"/> belongs
/// to the caller, which disposes it.
/// </para>
/// </remarks>
public sealed class DBusConnection : IAsyncDisposable
{
    private const string BusName = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";
    private readonly Wire _wire;
    private readonly CancellationTokenSource _disconnected = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, DBusObjectExport> _exports = new(StringComparer.Ordinal);
    private IDisposable? _closeWatch;
    private bool _disposed;

    private DBusConnection(Wire wire, string uniqueName)
    {
        _wire = wire;
        UniqueName = uniqueName;
    }

    /// <summary>Connects to the session or system bus.</summary>
    /// <returns>
    /// The connection, which the caller disposes; <see cref="PlatformUnavailableReason.BackendUnavailable"/> when the bus
    /// is not running or refuses the connection; <see cref="PlatformUnavailableReason.ProviderNotConfigured"/> on
    /// Windows and macOS, which have no D-Bus.
    /// </returns>
    public static ValueTask<PlatformResult<DBusConnection>> ConnectAsync(DBusBus bus, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(bus)) throw new ArgumentOutOfRangeException(nameof(bus));
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
            return ValueTask.FromResult<PlatformResult<DBusConnection>>(new PlatformResult<DBusConnection>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured));
        string? address = bus == DBusBus.Session ? DBusAddress.Session : DBusAddress.System;
        if (string.IsNullOrEmpty(address))
            return ValueTask.FromResult<PlatformResult<DBusConnection>>(new PlatformResult<DBusConnection>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        return ConnectCoreAsync(address, cancellationToken);
    }

    /// <summary>Connects to the bus or peer at a D-Bus address, such as <c>unix:path=/run/user/1000/bus</c>.</summary>
    /// <returns>The connection, which the caller disposes, or <see cref="PlatformUnavailableReason.BackendUnavailable"/>
    /// when nothing accepts the connection at the address.</returns>
    /// <exception cref="ArgumentException"><paramref name="address"/> is empty.</exception>
    public static ValueTask<PlatformResult<DBusConnection>> ConnectToAddressAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        cancellationToken.ThrowIfCancellationRequested();
        return ConnectCoreAsync(address, cancellationToken);
    }

    private static async ValueTask<PlatformResult<DBusConnection>> ConnectCoreAsync(string address, CancellationToken cancellationToken)
    {
        var wire = new Wire(address);
        try
        {
            await wire.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            var connection = new DBusConnection(wire, wire.UniqueName ?? "");
            await connection.WatchCloseAsync().ConfigureAwait(false);
            wire = null!;
            return new PlatformResult<DBusConnection>.Success(connection);
        }
        catch (Exception error) when (error is DBusExceptionBase or IOException or System.Net.Sockets.SocketException or ArgumentException)
        {
            return new PlatformResult<DBusConnection>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        finally { wire?.Dispose(); }
    }

    // A match that never matches a message but reports when the connection closes.
    private async ValueTask WatchCloseAsync()
    {
        _closeWatch = await _wire.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Interface = "org.runic.Never", Member = "Never" },
            static (_, _) => 0, (Notification<int> notification) => { if (notification.IsCompletion) OnClosed(); },
            emitOnCapturedContext: false, ObserverFlags.NoSubscribe | ObserverFlags.EmitOnConnectionClosed).ConfigureAwait(false);
    }

    private void OnClosed()
    {
        _disconnected.Cancel();
    }

    // The clock of call timeouts; tests replace it with a fake one to end a call at its timeout without waiting.
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>The connection's unique bus name, such as <c>:1.42</c>; empty on a peer-to-peer connection.</summary>
    public string UniqueName { get; }

    /// <summary>Cancelled when the connection closes, because the bus went away or the connection was disposed.</summary>
    public CancellationToken Disconnected => _disconnected.Token;

    /// <summary>Calls a method and returns its reply's values.</summary>
    /// <exception cref="DBusException">The peer replied with an error, or the connection closed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the call's reply is ignored.</exception>
    public async ValueTask<ImmutableArray<DBusValue>> CallMethodAsync(DBusMethodCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        Validate(call);
        cancellationToken.ThrowIfCancellationRequested();
        string signature = DBusWire.BodySignature(call.Arguments);
        var flags = (call.Options.HasFlag(DBusCallOptions.NoAutoStart) ? MessageFlags.NoAutoStart : MessageFlags.None)
            | (call.Options.HasFlag(DBusCallOptions.AllowInteractiveAuthorization) ? MessageFlags.AllowInteractiveAuthorization : MessageFlags.None);
        try
        {
            MessageBuffer message;
            var writer = _wire.GetMessageWriter();
            try
            {
                writer.WriteMethodCallHeader(call.Destination, call.Path.Value, call.Interface, call.Member, signature.Length == 0 ? null : signature, flags);
                DBusWire.WriteBody(ref writer, call.Arguments);
                message = writer.CreateMessage();
            }
            finally { writer.Dispose(); }
            var reply = _wire.CallMethodAsync(message, static (reply, _) => DBusWire.ReadBody(reply));
            try
            {
                return await (call.Timeout is { } timeout ? reply.WaitAsync(timeout, TimeProvider, cancellationToken) : reply.WaitAsync(cancellationToken))
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is TimeoutException || error is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                // A reply that arrives after cancellation or the timeout may carry file descriptors nobody will read.
                _ = reply.ContinueWith(static task => { foreach (var value in task.Result) DBusWire.CloseHandles(value); },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                if (error is TimeoutException)
                    throw new DBusException(DBusException.NoReply, $"{call.Interface}.{call.Member} got no reply within {call.Timeout}.", error);
                throw;
            }
        }
        catch (Exception error) when (Translate(error) is { } translated) { throw translated; }
    }

    /// <summary>Starts receiving the signals that match <paramref name="filter"/>.</summary>
    /// <returns>A subscription that is active when this completes, so no signal emitted afterwards is missed. Enumerate it
    /// once, and dispose it to stop receiving.</returns>
    /// <remarks>The bus applies the filter's keys, except <see cref="DBusSignalFilter.Arguments"/> and
    /// <see cref="DBusSignalFilter.ArgumentPaths"/> after the first argument, which the connection checks itself; the bus
    /// still sends those signals to the connection.</remarks>
    /// <exception cref="ArgumentException">An argument position is outside 0–63, <see cref="DBusSignalFilter.Arg0"/> and
    /// position 0 of <see cref="DBusSignalFilter.Arguments"/> differ, or <see cref="DBusSignalFilter.Arg0Namespace"/> is
    /// not a namespace of bus or interface names.</exception>
    /// <exception cref="DBusException">The bus refused the subscription, or the connection closed.</exception>
    public async ValueTask<DBusSignalSubscription> SubscribeAsync(DBusSignalFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var arguments = DBusSignalMatch.Check(filter);
        cancellationToken.ThrowIfCancellationRequested();
        var rule = new MatchRule
        {
            Type = MessageType.Signal, Sender = filter.Sender, Path = filter.Path?.Value, PathNamespace = filter.PathNamespace?.Value,
            Interface = filter.Interface, Member = filter.Member, Arg0 = filter.Arg0 ?? filter.Arguments.GetValueOrDefault(0),
            Arg0Path = filter.ArgumentPaths.GetValueOrDefault(0), Arg0Namespace = filter.Arg0Namespace,
        };
        var signals = Channel.CreateUnbounded<DBusSignal>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        try
        {
            var writer = signals.Writer;
            var match = await _wire.AddMatchAsync(rule, static (message, _) => ReadSignal(message), (Notification<DBusSignal> notification) =>
                {
                    // A signal that arrives after the subscription closed, or that the argument keys reject, is never read;
                    // close its descriptors.
                    if (notification.HasValue)
                    {
                        if (!(arguments?.Matches(notification.Value) ?? true) || !writer.TryWrite(notification.Value))
                            DBusSignalSubscription.CloseHandles(notification.Value);
                    }
                    else if (notification.IsCompletion) writer.TryComplete(Translate(notification.CompletionReason) ?? notification.CompletionReason);
                }, emitOnCapturedContext: false, ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed)
                .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            return new DBusSignalSubscription(match, signals);
        }
        catch (Exception error) when (Translate(error) is { } translated) { throw translated; }
    }

    private static DBusSignal ReadSignal(Message message) => new(message.SenderAsString, new DBusObjectPath(message.PathAsString ?? "/"),
        message.InterfaceAsString ?? "", message.MemberAsString ?? "", DBusWire.ReadBody(message));

    /// <summary>Asks the bus for a well-known name, such as <c>org.example.Player</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a well-known bus name.</exception>
    /// <exception cref="DBusException">The bus refused the request, for example because a policy forbids owning the name.</exception>
    public async ValueTask<DBusRequestNameReply> RequestNameAsync(string name, DBusRequestNameOptions options = DBusRequestNameOptions.None,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!DBusNames.IsWellKnownBusName(name)) throw new ArgumentException($"\"{name}\" is not a well-known bus name.", nameof(name));
        var reply = await CallBusAsync("RequestName", [DBusValue.From(name), DBusValue.From((uint)options)], cancellationToken).ConfigureAwait(false);
        return (DBusRequestNameReply)reply[0].GetUInt32();
    }

    /// <summary>Gives up a well-known name or leaves its queue.</summary>
    /// <returns>Whether the connection owned or queued for the name.</returns>
    public async ValueTask<bool> ReleaseNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!DBusNames.IsWellKnownBusName(name)) throw new ArgumentException($"\"{name}\" is not a well-known bus name.", nameof(name));
        var reply = await CallBusAsync("ReleaseName", [DBusValue.From(name)], cancellationToken).ConfigureAwait(false);
        return reply[0].GetUInt32() is 1;
    }

    /// <summary>Reports the unique name that owns a bus name: the current owner first, then every change.</summary>
    /// <remarks><see langword="null"/> means nobody owns the name, for example while its service is not running.</remarks>
    public async IAsyncEnumerable<string?> WatchNameOwnerAsync(string name, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!DBusNames.IsBusName(name)) throw new ArgumentException($"\"{name}\" is not a bus name.", nameof(name));
        await using var changes = await SubscribeAsync(new DBusSignalFilter
        {
            Sender = BusName, Path = new DBusObjectPath(BusPath), Interface = BusName, Member = "NameOwnerChanged", Arg0 = name,
        }, cancellationToken).ConfigureAwait(false);
        string? owner;
        try { owner = (await CallBusAsync("GetNameOwner", [DBusValue.From(name)], cancellationToken).ConfigureAwait(false))[0].GetString(); }
        catch (DBusException error) when (error.ErrorName == "org.freedesktop.DBus.Error.NameHasNoOwner") { owner = null; }
        yield return owner;
        await foreach (var change in changes.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            string? next = change.Arguments is [_, _, var newOwner] && newOwner.GetString() is { Length: > 0 } text ? text : null;
            if (next == owner) continue;
            owner = next;
            yield return owner;
        }
    }

    /// <summary>Makes <paramref name="service"/> answer calls at <paramref name="path"/> on this connection.</summary>
    /// <returns>The export; disposing it stops answering and lets the object be exported again.</returns>
    /// <exception cref="InvalidOperationException">Another object is exported at the path, the object is exported elsewhere, or
    /// its <see cref="DBusObject.MaxConcurrentCalls"/> is less than 1.</exception>
    public IAsyncDisposable Export(DBusObjectPath path, DBusObject service)
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_exports.ContainsKey(path.Value)) throw new InvalidOperationException($"An object is already exported at {path}.");
            var export = new DBusObjectExport(this, path, service);
            service.Attach(export);
            _exports.Add(path.Value, export);
            try { _wire.AddMethodHandler(export); }
            catch
            {
                _exports.Remove(path.Value);
                service.Detach(export);
                throw;
            }
            return export;
        }
    }

    internal void Unexport(DBusObjectExport export)
    {
        lock (_gate)
        {
            if (!_exports.TryGetValue(export.Path, out var current) || current != export) return;
            _exports.Remove(export.Path);
            if (!_disposed) _wire.RemoveMethodHandler(export.Path);
        }
    }

    internal bool TryEmitSignal(DBusObjectPath path, string interfaceName, string member, ImmutableArray<DBusValue> arguments)
    {
        string signature = DBusWire.BodySignature(arguments);
        var writer = _wire.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(null, path.Value, interfaceName, member, signature.Length == 0 ? null : signature);
            DBusWire.WriteBody(ref writer, arguments);
            return _wire.TrySendMessage(writer.CreateMessage());
        }
        finally { writer.Dispose(); }
    }

    private ValueTask<ImmutableArray<DBusValue>> CallBusAsync(string member, ImmutableArray<DBusValue> arguments, CancellationToken cancellationToken) =>
        CallMethodAsync(new DBusMethodCall(BusName, new DBusObjectPath(BusPath), BusName, member) { Arguments = arguments }, cancellationToken);

    private static void Validate(DBusMethodCall call)
    {
        if (call.Destination is { } destination && !DBusNames.IsBusName(destination))
            throw new ArgumentException($"\"{destination}\" is not a bus name.", nameof(call));
        if (!DBusNames.IsInterfaceName(call.Interface)) throw new ArgumentException($"\"{call.Interface}\" is not an interface name.", nameof(call));
        if (!DBusNames.IsMemberName(call.Member)) throw new ArgumentException($"\"{call.Member}\" is not a member name.", nameof(call));
    }

    internal static Exception? Translate(Exception error) => error switch
    {
        DBusException => null,
        DBusErrorReplyException reply => new DBusException(DBusNames.IsInterfaceName(reply.ErrorName) ? reply.ErrorName : DBusException.Failed, reply.ErrorMessage, reply),
        DBusConnectionException or ObjectDisposedException => new DBusException(DBusException.Disconnected, "The D-Bus connection is closed.", error),
        DBusMessageException => new DBusException(DBusException.InvalidArgs, "A D-Bus message could not be read.", error),
        _ => null,
    };

    /// <summary>Closes the connection. Pending calls fail with <see cref="DBusException.Disconnected"/> and subscriptions end.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _exports.Clear();
        }
        _closeWatch?.Dispose();
        _wire.Dispose();
        // The source stays undisposed so Disconnected remains readable after disposal.
        OnClosed();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Signals that match one filter, in the order the connection received them.</summary>
/// <remarks>Enumerate it once. Enumeration ends when the subscription is disposed, and throws <see cref="DBusException"/>
/// with <see cref="DBusException.Disconnected"/> when the connection closes. File descriptors in signals that are never
/// read, because the subscription was disposed or its enumeration stopped first, are closed when the subscription is
/// disposed; a descriptor in a signal that enumeration returned belongs to the reader.</remarks>
public sealed class DBusSignalSubscription : IAsyncEnumerable<DBusSignal>, IAsyncDisposable
{
    private readonly IDisposable _match;
    private readonly Channel<DBusSignal> _signals;
    private readonly object _drainGate = new();
    private int _enumerated;
    private int _reading;
    private int _disposed;

    internal DBusSignalSubscription(IDisposable match, Channel<DBusSignal> signals)
    {
        _match = match;
        _signals = signals;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The subscription is already being enumerated.</exception>
    public async IAsyncEnumerator<DBusSignal> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _enumerated, 1) != 0) throw new InvalidOperationException("A signal subscription can be enumerated once.");
        Volatile.Write(ref _reading, 1);
        try
        {
            var reader = _signals.Reader;
            while (true)
            {
                try
                {
                    if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) yield break;
                }
                catch (ChannelClosedException closed) when (closed.InnerException is { } inner) { throw inner; }
                while (reader.TryRead(out var signal)) yield return signal;
            }
        }
        finally
        {
            // The subscription drains what is left once nobody reads; whichever of this and DisposeAsync comes last does it.
            Interlocked.Exchange(ref _reading, 0);
            if (Volatile.Read(ref _disposed) != 0) DrainUnread();
        }
    }

    /// <summary>Stops receiving signals; an enumeration in progress ends after the signals already received.</summary>
    /// <remarks>Signals that were received but not read are dropped, and their file descriptors closed, once no enumeration
    /// is in progress.</remarks>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _match.Dispose();
        _signals.Writer.TryComplete();
        if (Volatile.Read(ref _reading) == 0) DrainUnread();
        return ValueTask.CompletedTask;
    }

    private void DrainUnread()
    {
        lock (_drainGate)
            while (_signals.Reader.TryRead(out var signal)) CloseHandles(signal);
    }

    internal static void CloseHandles(DBusSignal signal)
    {
        foreach (var argument in signal.Arguments) DBusWire.CloseHandles(argument);
    }
}

// The argument keys of a signal filter, checked by the connection: the bus applies only those of the first argument
// that Tmds.DBus.Protocol's match rules carry, and two subscriptions on one connection must not see each other's signals.
internal sealed class DBusSignalMatch
{
    private const int MaximumArgument = 63;
    private readonly ImmutableArray<(int Index, string Value)> _strings;
    private readonly ImmutableArray<(int Index, string Value)> _paths;
    private readonly string? _namespace;

    private DBusSignalMatch(ImmutableArray<(int, string)> strings, ImmutableArray<(int, string)> paths, string? @namespace)
    {
        _strings = strings;
        _paths = paths;
        _namespace = @namespace;
    }

    // Validates the filter; returns null when it has no argument keys.
    internal static DBusSignalMatch? Check(DBusSignalFilter filter)
    {
        foreach (var (index, value) in filter.Arguments.Concat(filter.ArgumentPaths))
        {
            if (index is < 0 or > MaximumArgument)
                throw new ArgumentException($"The argument position {index} is outside 0–{MaximumArgument}.", nameof(filter));
            ArgumentNullException.ThrowIfNull(value, nameof(filter));
        }
        if (filter.Arg0 is { } arg0 && filter.Arguments.TryGetValue(0, out var first) && !string.Equals(arg0, first, StringComparison.Ordinal))
            throw new ArgumentException("Arg0 and position 0 of Arguments differ; set one of them.", nameof(filter));
        if (filter.Arg0Namespace is { } ns && !DBusNames.IsNameNamespace(ns))
            throw new ArgumentException($"\"{ns}\" is not a namespace of bus or interface names.", nameof(filter));
        // Arg0 on its own keeps the matching it has always had, by the bus and Tmds.DBus.Protocol.
        var match = new DBusSignalMatch([.. filter.Arguments.Select(static entry => (entry.Key, entry.Value))],
            [.. filter.ArgumentPaths.Select(static entry => (entry.Key, entry.Value))], filter.Arg0Namespace);
        return match._strings.IsEmpty && match._paths.IsEmpty && match._namespace is null ? null : match;
    }

    internal bool Matches(DBusSignal signal)
    {
        var arguments = signal.Arguments;
        foreach (var (index, value) in _strings)
            if (index >= arguments.Length || arguments[index] is not { Signature.Value: "s" } argument
                || !string.Equals(argument.GetString(), value, StringComparison.Ordinal)) return false;
        foreach (var (index, value) in _paths)
        {
            if (index >= arguments.Length) return false;
            string? text = arguments[index] switch
            {
                { Signature.Value: "s" } argument => argument.GetString(),
                { Signature.Value: "o" } argument => argument.GetObjectPath().Value,
                _ => null,
            };
            if (text is null || !PathMatches(text, value)) return false;
        }
        if (_namespace is { } ns)
        {
            if (arguments.IsEmpty || arguments[0] is not { Signature.Value: "s" } first) return false;
            string name = first.GetString();
            if (!name.StartsWith(ns, StringComparison.Ordinal) || name.Length != ns.Length && name[ns.Length] != '.') return false;
        }
        return true;
    }

    // argNpath: equal, or one is a prefix of the other that ends in '/'.
    private static bool PathMatches(string argument, string value) =>
        string.Equals(argument, value, StringComparison.Ordinal)
        || value.EndsWith('/') && argument.StartsWith(value, StringComparison.Ordinal)
        || argument.EndsWith('/') && value.StartsWith(argument, StringComparison.Ordinal);
}
