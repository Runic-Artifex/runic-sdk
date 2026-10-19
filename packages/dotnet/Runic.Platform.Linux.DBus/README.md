# Runic.Platform.Linux.DBus

D-Bus client and service API for Runic applications on Linux: connections to the
session and system buses, typed values, calls, properties, signals, well-known names
and exported objects. Many Linux desktop integrations are plain D-Bus services, such
as UDisks2, UPower, NetworkManager and MPRIS media players; this package is how a
Runic application talks to them, or offers its own.

Targets .NET 10 and NativeAOT; it uses no reflection. A source generator turns
introspection XML into typed proxies and services. The wire protocol comes from
Tmds.DBus.Protocol, which stays an implementation detail: every public type belongs
to this package.

## Connect

Register the application's shared connections and take `IDBusConnections` where
you need a bus. Each bus is connected on first use, shared by every caller, and
closed when the service provider is disposed; a connection that closed, for example
because the bus restarted, is replaced on the next request. Do not dispose a shared
connection.

```csharp
services.AddRunicDBus();

public sealed class PowerStatus(IDBusConnections buses)
{
    public async Task<bool?> OnBatteryAsync(CancellationToken cancellationToken)
    {
        if (await buses.GetAsync(DBusBus.System, cancellationToken) is not PlatformResult<DBusConnection>.Success(var bus))
            return null; // No system bus, or not Linux.
        var upower = new DBusProxy(bus, "org.freedesktop.UPower", "/org/freedesktop/UPower");
        return (await upower.GetPropertyAsync("org.freedesktop.UPower", "OnBattery", cancellationToken)).GetBoolean();
    }
}
```

`DBusConnection.ConnectAsync(DBusBus.Session)` creates a connection the caller owns
and disposes; `ConnectToAddressAsync(address)` connects to any D-Bus address, such
as a private bus in tests. Connecting returns `PlatformResult<DBusConnection>`:
`Unavailable(BackendUnavailable)` when the bus is not running or refuses the
connection, and `Unavailable(ProviderNotConfigured)` on Windows and macOS.

## Values

`DBusValue` holds one value with its type signature. `DBusValue.From(...)` creates
basic values (`From(42)` is an `i`, `From(42u)` a `u`, `From("text")` an `s`), and
`Variant`, `Array`, `Dictionary`, `Struct`, `StringArray` and `VariantDictionary`
build containers. Read a value with the getter for its type, such as `GetInt32()`,
`GetItems()` or `GetVariantDictionary()`; another getter throws
`InvalidOperationException`. Values compare by signature and content.

| D-Bus | Create | Read |
| --- | --- | --- |
| `y` `b` `n` `q` `i` `u` `x` `t` `d` | `From(byte)` … `From(double)` | `GetByte()` … `GetDouble()` |
| `s` `o` `g` | `From(string)`, `From(DBusObjectPath)`, `From(DBusSignature)` | `GetString()`, `GetObjectPath()`, `GetTypeSignature()` |
| `h` | `UnixFd(SafeHandle)`; the caller keeps its handle | `GetUnixFd()`; a received descriptor is a `SafeFileHandle` that belongs to the receiver |
| `v` | `Variant(value)` | `GetVariant()` |
| `aT` | `Array(elementSignature, items)`, `StringArray`, `ObjectPathArray`, `ByteArray` | `GetItems()`, `GetStringArray()`, `GetObjectPathArray()`, `GetByteArray()` |
| `a{KV}` | `Dictionary(key, value, entries)`, `VariantDictionary` for `a{sv}` | `GetEntries()`, `GetVariantDictionary()` |
| `(...)` | `Struct(fields)` | `GetFields()` |

## Call, read properties and receive signals

A `DBusProxy` addresses one object of one service. `CallAsync(interface, member,
arguments)` returns the reply's values; `GetPropertyAsync`, `GetAllPropertiesAsync`
and `SetPropertyAsync` use `org.freedesktop.DBus.Properties`. Set
`DBusCallOptions.AllowInteractiveAuthorization` when the service may ask the user
for a password, as UDisks2 does through Polkit.

Signals and property changes arrive as `IAsyncEnumerable` streams, in the order the
connection received them. `SubscribeAsync`, `SubscribePropertiesAsync` and
`SubscribeSignalAsync` return a subscription that is already active, so read the
current state after subscribing and nothing is missed in between:

```csharp
var player = new DBusProxy(bus, "org.mpris.MediaPlayer2.vlc", "/org/mpris/MediaPlayer2");
await using var changes = await player.SubscribePropertiesAsync("org.mpris.MediaPlayer2.Player", cancellationToken);
var status = (await player.GetPropertyAsync("org.mpris.MediaPlayer2.Player", "PlaybackStatus", cancellationToken)).GetString();
await foreach (var signal in changes.WithCancellation(cancellationToken))
    if (DBusProxy.ReadPropertyChanges(signal) is { } change && change.Changed.TryGetValue("PlaybackStatus", out var value))
        status = value.GetString();
```

Disposing a subscription closes the file descriptors of signals it received but
nobody read. `DBusSignal`, `DBusPropertyChanges` and `DBusMethodCall` compare by
content, like the values they carry.

`WatchPropertiesAsync` and `WatchSignalAsync` are shorter forms that subscribe when
enumeration starts. `DBusConnection.WatchNameOwnerAsync(name)` reports a bus name's
owner, first the current one and then every change, so an application notices when
a service starts or stops.

## Offer a service

Derive from `DBusObject`, export it at a path and request a well-known name:

```csharp
sealed class Counter : DBusObject
{
    private int _value;
    protected override string IntrospectionXml => """
        <interface name="org.example.Counter"><method name="Add"><arg direction="in" type="i"/><arg direction="out" type="i"/></method>
        <property name="Value" type="i" access="read"/></interface>
        """;

    protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext call) => call switch
    {
        { Interface: "org.example.Counter", Member: "Add", Signature.Value: "i" } => Add(call.Arguments[0].GetInt32()),
        _ => throw new DBusException(DBusException.UnknownMethod, $"No method {call.Member}."),
    };

    private ValueTask<ImmutableArray<DBusValue>> Add(int amount)
    {
        int value = Interlocked.Add(ref _value, amount);
        EmitPropertiesChanged("org.example.Counter", new Dictionary<string, DBusValue> { ["Value"] = DBusValue.From(value) });
        return ValueTask.FromResult<ImmutableArray<DBusValue>>([DBusValue.From(value)]);
    }

    protected override ValueTask<DBusValue> GetPropertyAsync(string interfaceName, string name, DBusMethodContext call) =>
        name == "Value" ? ValueTask.FromResult(DBusValue.From(Volatile.Read(ref _value))) : base.GetPropertyAsync(interfaceName, name, call);
}

await using var export = bus.Export("/org/example/Counter", new Counter());
await bus.RequestNameAsync("org.example.Counter", DBusRequestNameOptions.DoNotQueue);
```

The base class answers `org.freedesktop.DBus.Introspectable` from
`IntrospectionXml` and `org.freedesktop.DBus.Properties` from the property methods;
the connection answers `org.freedesktop.DBus.Peer`. `EmitSignal` and
`EmitPropertiesChanged` send signals while the object is exported. Throw
`DBusException` to reply with an error name; any other exception replies
`org.freedesktop.DBus.Error.Failed` without its message, which can contain
internal details. Disposing the export stops answering at the path.

## Generate proxies and services

The package includes a source generator that turns D-Bus introspection XML into
typed C#. Save the service's interface XML in the project, for example from
`busctl --user introspect --xml-interface <service> <path>` or the service's
published XML, and list it as an `AdditionalFiles` item:

```xml
<ItemGroup>
  <AdditionalFiles Include="DBus/org.mpris.MediaPlayer2.Player.xml" RunicDBusMode="Proxy" Namespace="MyApp.DBus" />
</ItemGroup>
```

| Metadata | Values |
| --- | --- |
| `RunicDBusMode` | `Proxy`, `Service`, or `Proxy,Service` for both (a comma, not a semicolon). Required. |
| `Namespace` | The namespace of the generated types. Required. |
| `RunicDBusVisibility` | `internal` (default) or `public`. |
| `RunicDBusClassName` | The generated class's name, instead of one derived from the interface. |

A project that also references Tmds.DBus.Generator, which reads every `.xml`
`AdditionalFiles` item, sets `GenerateDBusTypes="false"` on the items this
generator owns, so the two do not generate the same interfaces twice.

**Proxy** generates one class per interface, named after the interface's last
segment (`Player` for `org.mpris.MediaPlayer2.Player`), deriving from
`DBusProxy`:

```csharp
var player = new Player(bus, "org.mpris.MediaPlayer2.vlc", "/org/mpris/MediaPlayer2");
await player.PlayPauseAsync();
double volume = await player.GetVolumeAsync();
await player.SetVolumeAsync(0.5);
PlayerProperties all = await player.GetPropertiesAsync();
await foreach (long position in player.WatchSeekedAsync(cancellationToken)) { /* … */ }
```

Each method becomes `<Method>Async`, with the call's options and a cancellation
token after its arguments; a method with several results returns a named tuple.
Each property becomes `Get<Property>Async` and, when writable,
`Set<Property>Async`. `GetPropertiesAsync` and `WatchPropertiesAsync` return
`<Name>Properties`, whose properties are `null` when the service did not send
them. Each signal becomes `Watch<Signal>Async` and a static `Read<Signal>` for
signals from `SubscribeSignalAsync`; a signal without arguments yields
`PlatformUnit`.

**Service** generates one abstract class for all interfaces of the file,
named after the first interface (`PlayerService`), deriving from `DBusObject`.
Derive from it and implement each method as `protected abstract
<Method>Async(arguments, DBusMethodContext context)`. Each property becomes a
C# property; setting it emits `PropertiesChanged` as the interface's
`org.freedesktop.DBus.Property.EmitsChangedSignal` annotation says (`true`,
`invalidates`, `const` or `false`), and a caller's write of a writable property
calls `OnSet<Property>Async`, which sets it by default. Each signal becomes
`Emit<Signal>(arguments)`. The class answers introspection with the file's
interfaces and checks each call's signature.

D-Bus types map to C# as follows: `y b n q i u x t d s` to `byte`, `bool`,
`short`, `ushort`, `int`, `uint`, `long`, `ulong`, `double` and `string`; `o`
and `g` to `DBusObjectPath` and `DBusSignature`; `h` to `SafeHandle`; `v` to
`DBusValue`; arrays to `ImmutableArray<T>`, dictionaries to
`ImmutableDictionary<TKey, TValue>` and structures to tuples. A reply or
property of another type than the XML declares throws `DBusException` with
`org.freedesktop.DBus.Error.InvalidArgs`. The generator reports problems in the
XML as [`RUNICDBUS` diagnostics](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#d-bus-source-generator).
Generated code uses only this package's public API and works with NativeAOT.

The generator adapts parts of Tmds.DBus.Generator, under the MIT License; the
package's `NOTICE` and `licenses/Tmds.DBus-LICENSE.txt` carry the notice.

## Threads and errors

No method captures the caller's synchronization context. Call results, signal
streams and service calls run on the thread pool; a ViewModel moves results to its
model context, for example with `await context.InvokeAsync(...)`. Service calls can
run concurrently, also with each other, so an object that keeps state guards it.

Method calls throw `DBusException` for an error reply: branch on `ErrorName`, such
as `org.freedesktop.DBus.Error.ServiceUnknown` or a service's own error. The
message is the peer's text, meant for logs. A closed connection reports
`DBusException.Disconnected`, and cancelling a call's token throws
`OperationCanceledException` and ignores the reply. Higher-level Runic services
built on this package report their outcomes as `PlatformResult`.

## Platforms

| Platform | Status |
| --- | --- |
| Linux | Session and system bus. Verified in CI on a private bus with JIT and NativeAOT. |
| Linux in Flatpak | Session bus names need `--talk-name` or `--own-name`, the system bus `--system-talk-name`; without them the bus refuses the calls with an access error. |
| Windows, macOS | Not applicable: there is no D-Bus. `ConnectAsync` returns `Unavailable(ProviderNotConfigured)`. |

Runic.Platform.Linux.Portal keeps its own internal D-Bus code for the portals and
uses this package only to activate desktop applications
(`org.freedesktop.Application`); both packages use the same Tmds.DBus.Protocol
version.
