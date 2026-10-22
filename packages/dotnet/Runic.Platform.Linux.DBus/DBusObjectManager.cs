using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Runic.Platform.Linux.DBus;

/// <summary>Reads the objects of a service that implements <c>org.freedesktop.DBus.ObjectManager</c>, such as UDisks2,
/// BlueZ or NetworkManager, and the interfaces they gain and lose.</summary>
/// <remarks>
/// <para>
/// Create it for the object manager's path, which the service documents; UDisks2's is
/// <c>/org/freedesktop/UDisks2</c>. <see cref="GetManagedObjectsAsync"/> returns every object below that path with the
/// properties of each of its interfaces, unwrapped from their variants.
/// </para>
/// <para>
/// To follow the objects without missing a change, call <see cref="SubscribeInterfaceChangesAsync"/> first, then
/// <see cref="GetManagedObjectsAsync"/>, then read each signal of the subscription with
/// <see cref="ReadInterfaceChanges"/>. Property changes of the objects themselves arrive as
/// <c>PropertiesChanged</c>; subscribe to them with <see cref="DBusConnection.SubscribeAsync"/> and a
/// <see cref="DBusSignalFilter.PathNamespace"/>.
/// </para>
/// </remarks>
public sealed class DBusObjectManagerProxy : DBusProxy
{
    /// <summary>The D-Bus interface name, <c>org.freedesktop.DBus.ObjectManager</c>.</summary>
    public const string InterfaceName = "org.freedesktop.DBus.ObjectManager";

    /// <summary>Creates a proxy for the object manager at <paramref name="path"/> of the service <paramref name="destination"/>.</summary>
    /// <param name="connection">The connection that carries the calls.</param>
    /// <param name="destination">The service's bus name, such as <c>org.freedesktop.UDisks2</c>, or <see langword="null"/> on a peer-to-peer connection.</param>
    /// <param name="path">The object manager.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not a bus name.</exception>
    public DBusObjectManagerProxy(DBusConnection connection, string? destination, DBusObjectPath path) : base(connection, destination, path) { }

    /// <summary>Reads every object the manager reports: each object's interfaces, with their properties unwrapped from
    /// their variants.</summary>
    /// <exception cref="DBusException">The object is no object manager, the reply has another type, or the service replied
    /// with another error.</exception>
    public async ValueTask<ImmutableDictionary<DBusObjectPath, ImmutableDictionary<string, ImmutableDictionary<string, DBusValue>>>> GetManagedObjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await CallAsync(InterfaceName, "GetManagedObjects", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (reply is not [{ Signature.Value: "a{oa{sa{sv}}}" } objects])
            throw new DBusException(DBusException.InvalidArgs, "ObjectManager.GetManagedObjects did not return a{oa{sa{sv}}}.");
        var result = ImmutableDictionary.CreateBuilder<DBusObjectPath, ImmutableDictionary<string, ImmutableDictionary<string, DBusValue>>>();
        // A repeated path or name replaces the earlier entry, so a malformed reply cannot fail with another exception.
        foreach (var entry in objects.GetEntries()) result[entry.Key.GetObjectPath()] = ReadInterfaces(entry.Value);
        return result.ToImmutable();
    }

    /// <summary>Starts receiving <c>InterfacesAdded</c> and <c>InterfacesRemoved</c> from the manager, in the order it emits them.</summary>
    /// <returns>A subscription that is active when this completes; read each signal with <see cref="ReadInterfaceChanges"/>.</returns>
    public ValueTask<DBusSignalSubscription> SubscribeInterfaceChangesAsync(CancellationToken cancellationToken = default) =>
        Connection.SubscribeAsync(new DBusSignalFilter { Sender = Destination, Path = Path, Interface = InterfaceName }, cancellationToken);

    /// <summary>Reports the interfaces objects gain and lose as they arrive.</summary>
    /// <remarks>The subscription starts when enumeration starts. To read the current objects without missing a change, call
    /// <see cref="SubscribeInterfaceChangesAsync"/> first, then <see cref="GetManagedObjectsAsync"/>, then enumerate the
    /// subscription.</remarks>
    public async IAsyncEnumerable<DBusInterfaceChanges> WatchInterfaceChangesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var subscription = await SubscribeInterfaceChangesAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var signal in subscription.WithCancellation(cancellationToken).ConfigureAwait(false))
            if (ReadInterfaceChanges(signal) is { } changes) yield return changes;
    }

    /// <summary>Reads an <c>InterfacesAdded</c> or <c>InterfacesRemoved</c> signal, or returns <see langword="null"/> for
    /// any other signal, including one whose arguments have other types.</summary>
    public static DBusInterfaceChanges? ReadInterfaceChanges(DBusSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Interface != InterfaceName) return null;
        return signal switch
        {
            { Member: "InterfacesAdded", Arguments: [{ Signature.Value: "o" } path, { Signature.Value: "a{sa{sv}}" } interfaces] } =>
                new DBusInterfaceChanges(path.GetObjectPath(), ReadInterfaces(interfaces), []),
            { Member: "InterfacesRemoved", Arguments: [{ Signature.Value: "o" } path, { Signature.Value: "as" } names] } =>
                new DBusInterfaceChanges(path.GetObjectPath(), ImmutableDictionary<string, ImmutableDictionary<string, DBusValue>>.Empty,
                    names.GetStringArray()),
            _ => null,
        };
    }

    private static ImmutableDictionary<string, ImmutableDictionary<string, DBusValue>> ReadInterfaces(DBusValue interfaces)
    {
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, DBusValue>>(StringComparer.Ordinal);
        foreach (var entry in interfaces.GetEntries())
        {
            var properties = ImmutableDictionary.CreateBuilder<string, DBusValue>(StringComparer.Ordinal);
            foreach (var property in entry.Value.GetEntries()) properties[property.Key.GetString()] = property.Value.GetVariant();
            result[entry.Key.GetString()] = properties.ToImmutable();
        }
        return result.ToImmutable();
    }
}

/// <summary>Interfaces one object gained or lost, from <c>org.freedesktop.DBus.ObjectManager</c>'s <c>InterfacesAdded</c>
/// or <c>InterfacesRemoved</c>.</summary>
/// <remarks><see cref="DBusObjectManagerProxy.ReadInterfaceChanges"/> creates them. An added set has
/// <see cref="Added"/> entries and no <see cref="Removed"/> names, a removed set the reverse. Changes compare by content:
/// the same path, the same added interfaces with equal properties, and the same removed names in the same order.</remarks>
public sealed class DBusInterfaceChanges : IEquatable<DBusInterfaceChanges>
{
    internal DBusInterfaceChanges(DBusObjectPath path, ImmutableDictionary<string, ImmutableDictionary<string, DBusValue>> added, ImmutableArray<string> removed)
    {
        Path = path;
        Added = added;
        Removed = removed.IsDefault ? [] : removed;
    }

    /// <summary>The object whose interfaces changed.</summary>
    public DBusObjectPath Path { get; }
    /// <summary>The interfaces the object gained, each with its properties unwrapped from their variants.</summary>
    public ImmutableDictionary<string, ImmutableDictionary<string, DBusValue>> Added { get; }
    /// <summary>The names of the interfaces the object lost. An object that lost all of them is gone.</summary>
    public ImmutableArray<string> Removed { get; }

    /// <summary>Compares two sets of changes by content.</summary>
    public bool Equals(DBusInterfaceChanges? other) =>
        ReferenceEquals(this, other) || other is not null && Path == other.Path && DBusContent.InterfacesEqual(Added, other.Added)
            && DBusContent.SequenceEqual(Removed, other.Removed);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DBusInterfaceChanges);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Path, DBusContent.InterfacesHash(Added), DBusContent.SequenceHash(Removed));

    /// <inheritdoc />
    public override string ToString() => $"{Path}: {Added.Count} added, {Removed.Length} removed";

    /// <summary>Compares two sets of changes by content.</summary>
    public static bool operator ==(DBusInterfaceChanges? left, DBusInterfaceChanges? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Compares two sets of changes by content.</summary>
    public static bool operator !=(DBusInterfaceChanges? left, DBusInterfaceChanges? right) => !(left == right);
}
