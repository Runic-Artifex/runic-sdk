using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Runic.Platform.Linux.DBus;

/// <summary>Calls one remote object: its methods, properties and signals.</summary>
/// <remarks>
/// Use a proxy directly with interface and member names, or derive a typed proxy for one interface whose methods call
/// the public call, property and signal methods of this class. A proxy holds no state on the bus and costs nothing to create; it fails only when used.
/// </remarks>
public class DBusProxy
{
    /// <summary>Creates a proxy for the object at <paramref name="path"/> of the service <paramref name="destination"/>.</summary>
    /// <param name="connection">The connection that carries the calls.</param>
    /// <param name="destination">The service's bus name, such as <c>org.freedesktop.UDisks2</c>, or <see langword="null"/> on a peer-to-peer connection.</param>
    /// <param name="path">The object.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not a bus name.</exception>
    public DBusProxy(DBusConnection connection, string? destination, DBusObjectPath path)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (destination is not null && !DBusNames.IsBusName(destination))
            throw new ArgumentException($"\"{destination}\" is not a bus name.", nameof(destination));
        Connection = connection;
        Destination = destination;
        Path = path;
    }

    /// <summary>The connection that carries the calls.</summary>
    public DBusConnection Connection { get; }
    /// <summary>The service's bus name.</summary>
    public string? Destination { get; }
    /// <summary>The object.</summary>
    public DBusObjectPath Path { get; }

    /// <summary>How long each call through this proxy waits for its reply, including property reads and writes;
    /// <see langword="null"/>, the default, waits until the bus or the service replies or the call is cancelled.</summary>
    /// <remarks>Set it when creating the proxy, such as <c>new DBusProxy(bus, name, path) { Timeout = TimeSpan.FromSeconds(5) }</c>.
    /// A call that times out throws <see cref="DBusException"/> with <see cref="DBusException.NoReply"/>; see
    /// <see cref="DBusMethodCall.Timeout"/>.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative other than
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>, or longer than <see cref="Task.WaitAsync(TimeSpan)"/> accepts.</exception>
    public TimeSpan? Timeout
    {
        get;
        init => field = DBusTimeouts.Check(value);
    }

    /// <summary>Calls a method of the object and returns its reply's values.</summary>
    /// <exception cref="DBusException">The object replied with an error, or the connection closed.</exception>
    public ValueTask<ImmutableArray<DBusValue>> CallAsync(string interfaceName, string member, ImmutableArray<DBusValue> arguments = default,
        DBusCallOptions options = DBusCallOptions.None, CancellationToken cancellationToken = default) =>
        Connection.CallMethodAsync(new DBusMethodCall(Destination, Path, interfaceName, member)
        {
            Arguments = arguments.IsDefault ? [] : arguments, Options = options, Timeout = Timeout,
        }, cancellationToken);

    /// <summary>Reads one property, unwrapped from its variant.</summary>
    /// <exception cref="DBusException">The object has no such property, or replied with another error.</exception>
    public async ValueTask<DBusValue> GetPropertyAsync(string interfaceName, string name, CancellationToken cancellationToken = default)
    {
        var reply = await CallAsync(DBusProperties.Interface, "Get", [DBusValue.From(interfaceName), DBusValue.From(name)],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return reply is [{ Signature.Value: "v" } value] ? value.GetVariant()
            : throw new DBusException(DBusException.InvalidArgs, "Properties.Get did not return one variant.");
    }

    /// <summary>Reads every property of an interface, each unwrapped from its variant.</summary>
    public async ValueTask<ImmutableDictionary<string, DBusValue>> GetAllPropertiesAsync(string interfaceName, CancellationToken cancellationToken = default)
    {
        var reply = await CallAsync(DBusProperties.Interface, "GetAll", [DBusValue.From(interfaceName)], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return reply is [{ Signature.Value: "a{sv}" } values] ? values.GetVariantDictionary()
            : throw new DBusException(DBusException.InvalidArgs, "Properties.GetAll did not return a{sv}.");
    }

    /// <summary>Writes one property; the method wraps the value in a variant.</summary>
    public async ValueTask SetPropertyAsync(string interfaceName, string name, DBusValue value, CancellationToken cancellationToken = default) =>
        await CallAsync(DBusProperties.Interface, "Set", [DBusValue.From(interfaceName), DBusValue.From(name), DBusValue.Variant(value)],
            cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>Starts receiving <c>PropertiesChanged</c> for one interface of the object.</summary>
    /// <returns>A subscription that is active when this completes; read each signal with <see cref="ReadPropertyChanges"/>.</returns>
    public ValueTask<DBusSignalSubscription> SubscribePropertiesAsync(string interfaceName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interfaceName);
        return Connection.SubscribeAsync(new DBusSignalFilter
        {
            Sender = Destination, Path = Path, Interface = DBusProperties.Interface, Member = "PropertiesChanged", Arg0 = interfaceName,
        }, cancellationToken);
    }

    /// <summary>Reports the property changes of one interface of the object as they arrive.</summary>
    /// <remarks>The subscription starts when enumeration starts. To read the current values without missing a change, call
    /// <see cref="SubscribePropertiesAsync"/> first, then <see cref="GetAllPropertiesAsync"/>, then enumerate the subscription.</remarks>
    public async IAsyncEnumerable<DBusPropertyChanges> WatchPropertiesAsync(string interfaceName, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var subscription = await SubscribePropertiesAsync(interfaceName, cancellationToken).ConfigureAwait(false);
        await foreach (var signal in subscription.WithCancellation(cancellationToken).ConfigureAwait(false))
            if (ReadPropertyChanges(signal) is { } changes) yield return changes;
    }

    /// <summary>Reads a <c>PropertiesChanged</c> signal, or returns <see langword="null"/> for any other signal.</summary>
    public static DBusPropertyChanges? ReadPropertyChanges(DBusSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Interface != DBusProperties.Interface || signal.Member != "PropertiesChanged"
            || signal.Arguments is not [{ Signature.Value: "s" } name, { Signature.Value: "a{sv}" } changed, { Signature.Value: "as" } invalidated])
            return null;
        return new(name.GetString(), changed.GetVariantDictionary(), invalidated.GetStringArray());
    }

    /// <summary>Starts receiving one signal of the object.</summary>
    /// <returns>A subscription that is active when this completes.</returns>
    public ValueTask<DBusSignalSubscription> SubscribeSignalAsync(string interfaceName, string member, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interfaceName);
        ArgumentNullException.ThrowIfNull(member);
        return Connection.SubscribeAsync(new DBusSignalFilter { Sender = Destination, Path = Path, Interface = interfaceName, Member = member },
            cancellationToken);
    }

    /// <summary>Reports the arguments of one signal of the object as it arrives; the subscription starts when enumeration starts.</summary>
    public async IAsyncEnumerable<ImmutableArray<DBusValue>> WatchSignalAsync(string interfaceName, string member,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var subscription = await SubscribeSignalAsync(interfaceName, member, cancellationToken).ConfigureAwait(false);
        await foreach (var signal in subscription.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return signal.Arguments;
    }
}
