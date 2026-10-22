using System.Collections.Immutable;
using System.Text;
using Tmds.DBus.Protocol;

namespace Runic.Platform.Linux.DBus;

/// <summary>An object that answers D-Bus calls once <see cref="DBusConnection.Export"/> exports it at a path.</summary>
/// <remarks>
/// <para>
/// Override <see cref="HandleMethodAsync"/> for the object's methods and the property methods for its properties. The
/// base class answers <c>org.freedesktop.DBus.Introspectable</c> from <see cref="IntrospectionXml"/> and
/// <c>org.freedesktop.DBus.Properties</c> from the property methods; the connection answers
/// <c>org.freedesktop.DBus.Peer</c>.
/// </para>
/// <para>
/// Calls run on the thread pool and may run concurrently, also with each other; an object that keeps state guards it,
/// or moves work to its model context. Throw <see cref="DBusException"/> to reply with an error name. Any other
/// exception replies <see cref="DBusException.Failed"/> without its message, which can contain internal details.
/// </para>
/// <para>
/// Any program on the bus can call an exported object, so the calls one object answers are bounded: at most
/// <see cref="MaxConcurrentCalls"/> run at once, and up to 64 more wait for a turn. Calls
/// start in the order they arrive. A call beyond both is refused with <see cref="DBusException.LimitsExceeded"/>.
/// </para>
/// </remarks>
public abstract class DBusObject
{
    // How many calls to one object can wait for a turn while MaxConcurrentCalls calls run.
    internal const int MaxWaitingCalls = 64;

    private DBusObjectExport? _export;

    /// <summary>The path the object is exported at, or <see langword="null"/> while it is not exported.</summary>
    public DBusObjectPath? Path => Volatile.Read(ref _export) is { } export ? new DBusObjectPath(export.Path) : (DBusObjectPath?)null;

    /// <summary>The most calls the object answers at once; 16 unless overridden.</summary>
    /// <remarks>
    /// <para>
    /// Calls start in the order they arrive. With 1, the object answers one call at a time, in that order, so a call
    /// sees what an earlier call changed. Raise the limit for an object whose calls wait a long time, such as for a user.
    /// </para>
    /// <para>
    /// While this many calls run, up to 64 more wait; the connection refuses further calls with
    /// <see cref="DBusException.LimitsExceeded"/> until one finishes. Introspection and calls whose arguments cannot be
    /// read are answered at once and do not count. The value is read when the object is exported.
    /// </para>
    /// </remarks>
    protected virtual int MaxConcurrentCalls => 16;

    /// <summary>The object's interfaces as introspection XML <c>&lt;interface&gt;</c> elements, without the standard interfaces.</summary>
    /// <remarks>Tools such as <c>busctl introspect</c> and generic clients read it. The default lists no interfaces.</remarks>
    protected virtual string IntrospectionXml => "";

    /// <summary>Answers a call of one of the object's methods with the reply's values, each carrying its signature.</summary>
    /// <remarks>Check <see cref="DBusMethodContext.Arguments"/> against the method's signature and throw
    /// <see cref="DBusException"/> with <see cref="DBusException.InvalidArgs"/> when it does not match; throw it with
    /// <see cref="DBusException.UnknownMethod"/> for a method the object does not have.</remarks>
    protected abstract ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context);

    /// <summary>Gets one property for <c>org.freedesktop.DBus.Properties.Get</c>.</summary>
    /// <remarks>The default reports <see cref="DBusException.UnknownProperty"/>.</remarks>
    protected virtual ValueTask<DBusValue> GetPropertyAsync(string interfaceName, string name, DBusMethodContext context) =>
        throw new DBusException(DBusException.UnknownProperty, $"No property {name} on {interfaceName}.");

    /// <summary>Gets every readable property of an interface for <c>org.freedesktop.DBus.Properties.GetAll</c>.</summary>
    /// <remarks>The default returns none.</remarks>
    protected virtual ValueTask<ImmutableDictionary<string, DBusValue>> GetAllPropertiesAsync(string interfaceName, DBusMethodContext context) =>
        ValueTask.FromResult(ImmutableDictionary<string, DBusValue>.Empty);

    /// <summary>Sets one property for <c>org.freedesktop.DBus.Properties.Set</c>.</summary>
    /// <remarks>The default reports <see cref="DBusException.UnknownProperty"/>. Report a read-only property with
    /// <see cref="DBusException.PropertyReadOnly"/>, and call <see cref="EmitPropertiesChanged"/> after a change.</remarks>
    protected virtual ValueTask SetPropertyAsync(string interfaceName, string name, DBusValue value, DBusMethodContext context) =>
        throw new DBusException(DBusException.UnknownProperty, $"No property {name} on {interfaceName}.");

    /// <summary>Emits a signal from the object.</summary>
    /// <returns>Whether the signal was sent: <see langword="false"/> while the object is not exported or after its connection closed.</returns>
    protected bool EmitSignal(string interfaceName, string member, params ReadOnlySpan<DBusValue> arguments)
    {
        ArgumentNullException.ThrowIfNull(interfaceName);
        ArgumentNullException.ThrowIfNull(member);
        if (!DBusNames.IsInterfaceName(interfaceName)) throw new ArgumentException($"\"{interfaceName}\" is not an interface name.", nameof(interfaceName));
        if (!DBusNames.IsMemberName(member)) throw new ArgumentException($"\"{member}\" is not a member name.", nameof(member));
        var values = arguments.ToImmutableArray();
        _ = DBusWire.BodySignature(values);
        return Volatile.Read(ref _export) is { } export && export.TryEmit(interfaceName, member, values);
    }

    /// <summary>Emits <c>org.freedesktop.DBus.Properties.PropertiesChanged</c> for properties of one interface.</summary>
    /// <param name="interfaceName">The interface whose properties changed.</param>
    /// <param name="changed">The new values; the method wraps each in a variant.</param>
    /// <param name="invalidated">Properties that changed without sending their new value.</param>
    /// <returns>Whether the signal was sent.</returns>
    protected bool EmitPropertiesChanged(string interfaceName, IReadOnlyDictionary<string, DBusValue> changed, IEnumerable<string>? invalidated = null)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return EmitSignal(DBusProperties.Interface, "PropertiesChanged", DBusValue.From(interfaceName), DBusValue.VariantDictionary(changed),
            DBusValue.StringArray(invalidated ?? []));
    }

    internal void Attach(DBusObjectExport export)
    {
        if (Interlocked.CompareExchange(ref _export, export, null) is not null)
            throw new InvalidOperationException("The object is already exported.");
    }

    internal void Detach(DBusObjectExport export) => Interlocked.CompareExchange(ref _export, null, export);

    internal ValueTask<ImmutableArray<DBusValue>> InvokeAsync(DBusMethodContext context) => HandleMethodAsync(context);
    internal ValueTask<DBusValue> GetAsync(string interfaceName, string name, DBusMethodContext context) => GetPropertyAsync(interfaceName, name, context);
    internal ValueTask<ImmutableDictionary<string, DBusValue>> GetAllAsync(string interfaceName, DBusMethodContext context) => GetAllPropertiesAsync(interfaceName, context);
    internal ValueTask SetAsync(string interfaceName, string name, DBusValue value, DBusMethodContext context) => SetPropertyAsync(interfaceName, name, value, context);
    internal string Introspection => IntrospectionXml;
    internal int ConcurrencyLimit => MaxConcurrentCalls;
}

/// <summary>One call to an exported <see cref="DBusObject"/>.</summary>
public sealed class DBusMethodContext
{
    internal DBusMethodContext(string? sender, DBusObjectPath path, string interfaceName, string member, ImmutableArray<DBusValue> arguments,
        CancellationToken requestAborted)
    {
        Sender = sender;
        Path = path;
        Interface = interfaceName;
        Member = member;
        Arguments = arguments;
        RequestAborted = requestAborted;
    }

    /// <summary>The unique bus name of the caller.</summary>
    public string? Sender { get; }
    /// <summary>The object path the call addressed.</summary>
    public DBusObjectPath Path { get; }
    /// <summary>The interface the call addressed; empty when the caller named none.</summary>
    public string Interface { get; }
    /// <summary>The method name.</summary>
    public string Member { get; }
    /// <summary>The call's arguments. File descriptors among them belong to the object, which disposes them.</summary>
    public ImmutableArray<DBusValue> Arguments { get; }
    /// <summary>Cancelled when the connection closes; a reply then can no longer be sent.</summary>
    public CancellationToken RequestAborted { get; }

    /// <summary>The arguments' signature, such as <c>sa{sv}</c>.</summary>
    public DBusSignature Signature => new(string.Concat(Arguments.Select(static argument => argument.Signature.Value)), trusted: true);
}

internal static class DBusProperties
{
    internal const string Interface = "org.freedesktop.DBus.Properties";
    internal const string Introspectable = "org.freedesktop.DBus.Introspectable";

    internal static readonly ReadOnlyMemory<byte> StandardXml = Encoding.UTF8.GetBytes("""
          <interface name="org.freedesktop.DBus.Properties">
            <method name="Get"><arg direction="in" type="s" name="interface_name"/><arg direction="in" type="s" name="property_name"/><arg direction="out" type="v" name="value"/></method>
            <method name="GetAll"><arg direction="in" type="s" name="interface_name"/><arg direction="out" type="a{sv}" name="properties"/></method>
            <method name="Set"><arg direction="in" type="s" name="interface_name"/><arg direction="in" type="s" name="property_name"/><arg direction="in" type="v" name="value"/></method>
            <signal name="PropertiesChanged"><arg type="s" name="interface_name"/><arg type="a{sv}" name="changed_properties"/><arg type="as" name="invalidated_properties"/></signal>
          </interface>
          <interface name="org.freedesktop.DBus.Introspectable">
            <method name="Introspect"><arg direction="out" type="s" name="xml_data"/></method>
          </interface>
          <interface name="org.freedesktop.DBus.Peer">
            <method name="Ping"/>
            <method name="GetMachineId"><arg direction="out" type="s" name="machine_uuid"/></method>
          </interface>

        """);
}

// Routes the calls for one exported path to its object, off the connection's read loop. At most `_limit` calls run at once;
// later calls wait in arrival order, up to MaxWaitingCalls, and any beyond are refused.
internal sealed class DBusObjectExport : IPathMethodHandler, IAsyncDisposable
{
    private readonly DBusConnection connection;
    private readonly DBusObjectPath path;
    private readonly DBusObject service;
    private readonly int _limit;
    private readonly Lock _calls = new();
    private readonly Queue<(MethodContext Context, DBusMethodContext Call)> _waiting = new();
    private int _running;
    private int _disposed;

    internal DBusObjectExport(DBusConnection connection, DBusObjectPath path, DBusObject service)
    {
        this.connection = connection;
        this.path = path;
        this.service = service;
        _limit = service.ConcurrencyLimit;
        if (_limit < 1) throw new InvalidOperationException($"The object's MaxConcurrentCalls is {_limit}; it must be at least 1.");
        Path = path.Value;
    }

    public string Path { get; }
    public bool HandlesChildPaths => false;

    internal bool TryEmit(string interfaceName, string member, ImmutableArray<DBusValue> arguments) =>
        Volatile.Read(ref _disposed) == 0 && connection.TryEmitSignal(path, interfaceName, member, arguments);

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        if (context.IsDBusIntrospectRequest)
        {
            var xml = service.Introspection;
            context.ReplyIntrospectXml([Encoding.UTF8.GetBytes(xml), DBusProperties.StandardXml]);
            return ValueTask.CompletedTask;
        }
        ImmutableArray<DBusValue> arguments;
        try { arguments = DBusWire.ReadBody(request); }
        catch (DBusException error)
        {
            context.ReplyError(error.ErrorName, error.Message);
            return ValueTask.CompletedTask;
        }
        var call = new DBusMethodContext(request.SenderAsString, path, request.InterfaceAsString ?? "", request.MemberAsString ?? "", arguments,
            context.RequestAborted);
        bool start, refuse = false;
        lock (_calls)
        {
            start = _running < _limit;
            if (start) _running++;
            else if (_waiting.Count >= DBusObject.MaxWaitingCalls) refuse = true;
            else
            {
                // The connection reads no further message until this method returns: answer on the thread pool instead.
                context.DisposesAsynchronously = true;
                _waiting.Enqueue((context, call));
            }
        }
        if (refuse)
        {
            // Any program on the bus can call the object: refuse a flood instead of queueing it.
            foreach (var argument in arguments) DBusWire.CloseHandles(argument);
            context.ReplyError(DBusException.LimitsExceeded, "The object has too many calls in progress.");
        }
        else if (start)
        {
            context.DisposesAsynchronously = true;
            _ = Task.Run(() => AnswerAsync(context, call));
        }
        return ValueTask.CompletedTask;
    }

    // Starts the longest-waiting call in the finished call's place, or frees the place.
    private void Next()
    {
        (MethodContext Context, DBusMethodContext Call) next;
        lock (_calls)
        {
            if (!_waiting.TryDequeue(out next))
            {
                _running--;
                return;
            }
        }
        _ = Task.Run(() => AnswerAsync(next.Context, next.Call));
    }

    private async Task AnswerAsync(MethodContext context, DBusMethodContext call)
    {
        try { await ReplyAsync(context, call).ConfigureAwait(false); }
        finally { Next(); }
    }

    private async Task ReplyAsync(MethodContext context, DBusMethodContext call)
    {
        try
        {
            ImmutableArray<DBusValue> reply = call.Interface == DBusProperties.Interface ? await PropertiesAsync(call).ConfigureAwait(false)
                : await service.InvokeAsync(call).ConfigureAwait(false);
            string signature = DBusWire.BodySignature(reply);
            var writer = context.CreateReplyWriter(signature.Length == 0 ? null : signature);
            try
            {
                DBusWire.WriteBody(ref writer, reply);
                context.Reply(writer.CreateMessage());
            }
            finally { writer.Dispose(); }
        }
        catch (DBusException error) { TryReplyError(context, error.ErrorName, error.Message); }
        catch (Exception) { TryReplyError(context, DBusException.Failed, "The method failed."); }
        finally
        {
            try { context.Dispose(); }
            catch (ObjectDisposedException) { }
        }
    }

    private static void TryReplyError(MethodContext context, string name, string message)
    {
        try { context.ReplyError(name, message); }
        catch (Exception error) when (error is ObjectDisposedException or DBusExceptionBase) { }
    }

    private async ValueTask<ImmutableArray<DBusValue>> PropertiesAsync(DBusMethodContext call)
    {
        switch (call.Member, call.Signature.Value)
        {
            case ("Get", "ss"):
                var value = await service.GetAsync(call.Arguments[0].GetString(), call.Arguments[1].GetString(), call).ConfigureAwait(false);
                return [DBusValue.Variant(value)];
            case ("GetAll", "s"):
                var all = await service.GetAllAsync(call.Arguments[0].GetString(), call).ConfigureAwait(false);
                return [DBusValue.VariantDictionary(all)];
            case ("Set", "ssv"):
                await service.SetAsync(call.Arguments[0].GetString(), call.Arguments[1].GetString(), call.Arguments[2].GetVariant(), call).ConfigureAwait(false);
                return [];
            case ("Get" or "GetAll" or "Set", _):
                throw new DBusException(DBusException.InvalidArgs, $"{call.Member} does not take \"{call.Signature}\".");
            default:
                throw new DBusException(DBusException.UnknownMethod, $"No method {call.Member} on {DBusProperties.Interface}.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            connection.Unexport(this);
            service.Detach(this);
        }
        return ValueTask.CompletedTask;
    }
}
