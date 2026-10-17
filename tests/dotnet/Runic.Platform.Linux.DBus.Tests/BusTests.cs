using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;
using Runic.Platform;
using Runic.Platform.Linux.DBus;
using TUnit.Core;
using static ValueTests;

// A service and a client on a private session bus. Run under dbus-run-session; never against a desktop session.
internal sealed class BusTests
{
    private const string Interface = "org.runic.Test.Echo";
    private const string ServiceName = "org.runic.Test.Service";
    private static readonly DBusObjectPath ObjectPath = new("/org/runic/Test/Echo");

    [Test, Explicit, Category("DBus"), DisplayName("every value type survives a call and its reply")]
    public async Task RoundTripAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        var echo = new EchoObject();
        await using var export = service.Export(ObjectPath, echo);
        var properties = DBusValue.VariantDictionary([KeyValuePair.Create("Name", DBusValue.From("Ω disk")), KeyValuePair.Create("Size", DBusValue.From(1UL << 40)),
            KeyValuePair.Create("Nested", DBusValue.Variant(DBusValue.StringArray(["a", "b"])))]);
        ImmutableArray<DBusValue> arguments =
        [
            DBusValue.From((byte)255), DBusValue.From(true), DBusValue.From(short.MinValue), DBusValue.From(ushort.MaxValue), DBusValue.From(int.MinValue),
            DBusValue.From(uint.MaxValue), DBusValue.From(long.MinValue), DBusValue.From(ulong.MaxValue), DBusValue.From(-0.25), DBusValue.From("text ✓"),
            DBusValue.From(new DBusObjectPath("/org/example")), DBusValue.From(new DBusSignature("a(sv)")), DBusValue.Variant(DBusValue.From(7)),
            DBusValue.ByteArray([0, 1, 2, 255]), DBusValue.Array("(si)", [DBusValue.Struct(DBusValue.From("a"), DBusValue.From(1)), DBusValue.Struct(DBusValue.From("b"), DBusValue.From(2))]),
            DBusValue.Dictionary("o", "a{sv}", [KeyValuePair.Create(DBusValue.From(new DBusObjectPath("/d/1")), properties)]),
            DBusValue.Struct(DBusValue.From(1.5), DBusValue.Struct(DBusValue.From("inner"), DBusValue.Array("x", []))), DBusValue.Array("s", []),
        ];
        var reply = await new DBusProxy(client, service.UniqueName, ObjectPath).CallAsync(Interface, "Echo", arguments);
        Check(reply.SequenceEqual(arguments), $"The reply differs: {string.Join(", ", reply)}");
        Check(echo.Last is { Member: "Echo", Interface: Interface } call && call.Sender == client.UniqueName && call.Signature.Value == string.Concat(arguments.Select(static a => a.Signature.Value)),
            "The service saw the call's member, interface, sender and signature.");
    }

    [Test, Explicit, Category("DBus"), DisplayName("a file descriptor reaches the service, which owns its copy")]
    public async Task FileDescriptorAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        await using var export = service.Export(ObjectPath, new EchoObject());
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "descriptor");
            using var handle = File.OpenHandle(file);
            var reply = await new DBusProxy(client, service.UniqueName, ObjectPath).CallAsync(Interface, "ReadFd", [DBusValue.UnixFd(handle)]);
            Check(reply is [var text] && text.GetString() == "descriptor", "The service read the passed descriptor.");
            Check(!handle.IsClosed, "Sending the descriptor closed the caller's handle.");
        }
        finally { File.Delete(file); }
    }

    [Test, Explicit, Category("DBus"), DisplayName("errors, unknown members, cancellation and concurrent calls")]
    public async Task ErrorsAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        var echo = new EchoObject();
        await using var export = service.Export(ObjectPath, echo);
        var proxy = new DBusProxy(client, service.UniqueName, ObjectPath);
        Check(await ErrorAsync(() => proxy.CallAsync(Interface, "Fail")) is { ErrorName: "org.runic.Test.Error.Refused", Message: "No." }, "a named error");
        Check(await ErrorAsync(() => proxy.CallAsync(Interface, "Crash")) is { ErrorName: DBusException.Failed, Message: "The method failed." },
            "an unexpected exception replies Failed without its message");
        Check(await ErrorAsync(() => proxy.CallAsync(Interface, "Missing")) is { ErrorName: DBusException.UnknownMethod }, "an unknown method");
        var unknown = await ErrorAsync(() => new DBusProxy(client, "org.runic.Test.Nobody", ObjectPath).CallAsync(Interface, "Echo", options: DBusCallOptions.NoAutoStart));
        Check(unknown is { ErrorName: "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner" }, $"an unknown service: {unknown?.ErrorName} {unknown?.Message}");
        Check(await ErrorAsync(() => new DBusProxy(client, service.UniqueName, "/org/runic/Test/Other").CallAsync(Interface, "Echo"))
            is { ErrorName: DBusException.UnknownMethod or "org.freedesktop.DBus.Error.UnknownObject" }, "an object that is not exported");

        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try { await proxy.CallAsync(Interface, "Sleep", [DBusValue.From(5000)], cancellationToken: cancellation.Token); throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        // Calls run concurrently and off the connection's read loop, even a handler that blocks its thread: two
        // half-second calls finish well before a second.
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(proxy.CallAsync(Interface, "Block", [DBusValue.From(500)]).AsTask(), proxy.CallAsync(Interface, "Block", [DBusValue.From(500)]).AsTask());
        Check(clock.ElapsedMilliseconds < 950, $"Calls ran one after another: {clock.ElapsedMilliseconds} ms.");

        Throws<ArgumentException>(() => _ = new DBusProxy(client, "not a name", ObjectPath), "invalid destination");
        await ThrowsAsync<ArgumentException>(() => proxy.CallAsync("NoDots", "Echo").AsTask(), "invalid interface");
        await ThrowsAsync<ArgumentException>(() => proxy.CallAsync(Interface, "Bad-Member").AsTask(), "invalid member");
    }

    [Test, Explicit, Category("DBus"), DisplayName("properties: get, get all, set, read-only and PropertiesChanged in order")]
    public async Task PropertiesAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        var echo = new EchoObject();
        await using var export = service.Export(ObjectPath, echo);
        var proxy = new DBusProxy(client, service.UniqueName, ObjectPath);
        Check((await proxy.GetPropertyAsync(Interface, "Volume")).GetDouble() == 0.5, "Get");
        var all = await proxy.GetAllPropertiesAsync(Interface);
        Check(all.Count == 2 && all["Name"].GetString() == "echo", "GetAll");
        Check(await ErrorAsync(() => proxy.GetPropertyAsync(Interface, "Missing").AsTask()) is { ErrorName: DBusException.UnknownProperty }, "unknown property");
        Check(await ErrorAsync(() => proxy.SetPropertyAsync(Interface, "Name", DBusValue.From("x")).AsTask()) is { ErrorName: DBusException.PropertyReadOnly }, "read-only");

        await using var changes = await proxy.SubscribePropertiesAsync(Interface);
        await proxy.SetPropertyAsync(Interface, "Volume", DBusValue.From(0.75));
        Check(echo.Volume == 0.75, "Set reached the object.");
        for (int index = 0; index < 50; index++) echo.Announce(index);
        var received = new List<DBusPropertyChanges>();
        await using (var enumerator = changes.GetAsyncEnumerator())
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (received.Count < 51 && await enumerator.MoveNextAsync().AsTask().WaitAsync(deadline.Token))
                if (DBusProxy.ReadPropertyChanges(enumerator.Current) is { } change) received.Add(change);
        }
        Check(received[0].Changed["Volume"].GetDouble() == 0.75 && received[0].Interface == Interface, "The set's change arrived first.");
        Check(received.Skip(1).Select(static change => change.Changed["Counter"].GetInt32()).SequenceEqual(Enumerable.Range(0, 50))
            && received[1].Invalidated.SequenceEqual(["Name"]), "Changes arrived in order with their invalidated names.");
    }

    [Test, Explicit, Category("DBus"), DisplayName("signals, introspection, unexport and the object's path")]
    public async Task SignalsAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        var echo = new EchoObject();
        Check(echo.Path is null && !echo.Ping("unexported"), "An unexported object emits nothing.");
        var export = service.Export(ObjectPath, echo);
        Check(echo.Path == ObjectPath, "Path while exported.");
        Throws<InvalidOperationException>(() => service.Export(ObjectPath, new EchoObject()), "a second object at the path");
        Throws<InvalidOperationException>(() => service.Export("/org/runic/Test/Two", echo), "the object at a second path");
        var proxy = new DBusProxy(client, service.UniqueName, ObjectPath);
        await using (var pings = await proxy.SubscribeSignalAsync(Interface, "Pinged"))
        {
            Check(echo.Ping("one") && echo.Ping("two"), "Emitting while exported.");
            var texts = new List<string>();
            await foreach (var signal in pings.WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token))
            {
                Check(signal.Sender == service.UniqueName && signal.Path == ObjectPath, "signal origin");
                texts.Add(signal.Arguments[0].GetString());
                if (texts.Count == 2) break;
            }
            Check(texts.SequenceEqual(["one", "two"]), "signals in order");
        }
        var xml = (await proxy.CallAsync("org.freedesktop.DBus.Introspectable", "Introspect"))[0].GetString();
        Check(xml.Contains($"<interface name=\"{Interface}\">", StringComparison.Ordinal) && xml.Contains("org.freedesktop.DBus.Properties", StringComparison.Ordinal),
            "introspection lists the object's and the standard interfaces");
        var parent = (await new DBusProxy(client, service.UniqueName, "/org/runic/Test").CallAsync("org.freedesktop.DBus.Introspectable", "Introspect"))[0].GetString();
        Check(parent.Contains("<node name=\"Echo\"/>", StringComparison.Ordinal), "the parent path lists the child");
        Check((await proxy.CallAsync("org.freedesktop.DBus.Peer", "Ping")).IsEmpty, "the connection answers Peer.Ping");
        await export.DisposeAsync();
        Check(echo.Path is null && !echo.Ping("gone"), "Unexport detaches the object.");
        Check(await ErrorAsync(() => proxy.CallAsync(Interface, "Echo")) is { ErrorName: DBusException.UnknownMethod or "org.freedesktop.DBus.Error.UnknownObject" },
            "an unexported object no longer answers");
        await using var again = service.Export(ObjectPath, echo);
        Check((await proxy.CallAsync(Interface, "Echo", [DBusValue.From(1)]))[0].GetInt32() == 1, "the object can be exported again");
    }

    [Test, Explicit, Category("DBus"), DisplayName("well-known names: request, queue, release and owner changes")]
    public async Task NamesAsync()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();
        await using var watcher = await ConnectAsync();
        var owners = watcher.WatchNameOwnerAsync(ServiceName).GetAsyncEnumerator();
        Check(await owners.MoveNextAsync() && owners.Current is null, "nobody owns the name at first");
        Check(await first.RequestNameAsync(ServiceName, DBusRequestNameOptions.DoNotQueue) == DBusRequestNameReply.PrimaryOwner, "first owner");
        Check(await first.RequestNameAsync(ServiceName) == DBusRequestNameReply.AlreadyOwner, "already owner");
        Check(await second.RequestNameAsync(ServiceName, DBusRequestNameOptions.DoNotQueue) == DBusRequestNameReply.Exists, "taken");
        Check(await second.RequestNameAsync(ServiceName) == DBusRequestNameReply.InQueue, "queued");
        Check(await owners.MoveNextAsync() && owners.Current == first.UniqueName, "the first owner is reported");
        Check(await first.ReleaseNameAsync(ServiceName), "released");
        Check(await owners.MoveNextAsync() && owners.Current == second.UniqueName, "the queued connection takes over");
        await second.DisposeAsync();
        Check(await owners.MoveNextAsync() && owners.Current is null, "a closed owner leaves the name unowned");
        await owners.DisposeAsync();
        await ThrowsAsync<ArgumentException>(() => first.RequestNameAsync(":1.5").AsTask(), "a unique name cannot be requested");
    }

    [Test, Explicit, Category("DBus"), DisplayName("closing a connection ends calls and subscriptions")]
    public async Task DisconnectAsync()
    {
        var connection = await ConnectAsync();
        await using var service = await ConnectAsync();
        await using var export = service.Export(ObjectPath, new EchoObject());
        var subscription = await connection.SubscribeAsync(new DBusSignalFilter { Interface = Interface });
        var pending = new DBusProxy(connection, service.UniqueName, ObjectPath).CallAsync(Interface, "Sleep", [DBusValue.From(5000)]).AsTask();
        await Task.Delay(100);
        await connection.DisposeAsync();
        Check(connection.Disconnected.IsCancellationRequested, "Disconnected after disposal");
        Check(await ErrorAsync(() => pending) is { ErrorName: DBusException.Disconnected }, "a pending call fails with Disconnected");
        Check(await ErrorAsync(() => new DBusProxy(connection, service.UniqueName, ObjectPath).CallAsync(Interface, "Echo").AsTask()) is { ErrorName: DBusException.Disconnected },
            "a later call fails with Disconnected");
        try
        {
            await foreach (var _ in subscription.WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token)) { }
        }
        catch (DBusException error) when (error.ErrorName == DBusException.Disconnected) { }
        await subscription.DisposeAsync();
    }

    [Test, Explicit, Category("DBus"), DisplayName("dependency injection shares one connection per bus for the application")]
    public async Task DependencyInjectionAsync()
    {
        DBusConnection shared;
        await using (var provider = new ServiceCollection().AddRunicDBus().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
        {
            var connections = provider.GetRequiredService<IDBusConnections>();
            shared = ((PlatformResult<DBusConnection>.Success)await connections.GetAsync(DBusBus.Session)).Value;
            Check(await connections.GetAsync(DBusBus.Session) is PlatformResult<DBusConnection>.Success(var again) && ReferenceEquals(again, shared), "one session connection");
            await using var scope = provider.CreateAsyncScope();
            Check(ReferenceEquals(scope.ServiceProvider.GetRequiredService<IDBusConnections>(), connections), "an application singleton");
        }
        Check(shared.Disconnected.IsCancellationRequested, "disposing the provider closed the connection");
        var custom = new FixedConnections();
        Check(ReferenceEquals(new ServiceCollection().AddSingleton<IDBusConnections>(custom).AddRunicDBus().BuildServiceProvider().GetRequiredService<IDBusConnections>(), custom),
            "an existing registration is kept");
    }

    private static async Task<DBusConnection> ConnectAsync() =>
        await DBusConnection.ConnectAsync(DBusBus.Session) is PlatformResult<DBusConnection>.Success(var connection) ? connection
            : throw new InvalidOperationException("Run the bus cases under dbus-run-session.");

    private static async Task<DBusException?> ErrorAsync(Func<Task> call)
    {
        try { await call(); return null; }
        catch (DBusException error) { return error; }
    }

    private static Task<DBusException?> ErrorAsync<T>(Func<ValueTask<T>> call) => ErrorAsync((Func<Task>)(async () => await call()));

    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}: {message}");
    }

    private sealed class FixedConnections : IDBusConnections
    {
        public ValueTask<PlatformResult<DBusConnection>> GetAsync(DBusBus bus, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlatformResult<DBusConnection>>(new PlatformResult<DBusConnection>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
    }

    // Echoes its arguments and exposes two properties, the way a generated service would.
    private sealed class EchoObject : DBusObject
    {
        private int _counter;
        public double Volume { get; private set; } = 0.5;
        public DBusMethodContext? Last { get; private set; }

        protected override string IntrospectionXml => $"""<interface name="{Interface}"><method name="Echo"/><signal name="Pinged"><arg type="s"/></signal></interface>""";

        protected override async ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context)
        {
            Last = context;
            if (context.Interface != Interface) throw new DBusException(DBusException.UnknownInterface, context.Interface);
            switch (context.Member)
            {
                case "Echo": return context.Arguments;
                case "Fail": throw new DBusException("org.runic.Test.Error.Refused", "No.");
                case "Crash": throw new InvalidOperationException("internal detail /secret/path");
                case "Sleep": await Task.Delay(context.Arguments[0].GetInt32(), context.RequestAborted); return [];
                case "Block": Thread.Sleep(context.Arguments[0].GetInt32()); return [];
                case "ReadFd":
                    using (var handle = (SafeFileHandle)context.Arguments[0].GetUnixFd())
                    using (var stream = new FileStream(handle, FileAccess.Read))
                    using (var reader = new StreamReader(stream))
                        return [DBusValue.From(await reader.ReadToEndAsync())];
                default: throw new DBusException(DBusException.UnknownMethod, context.Member);
            }
        }

        protected override ValueTask<DBusValue> GetPropertyAsync(string interfaceName, string name, DBusMethodContext context) => name switch
        {
            "Volume" => ValueTask.FromResult(DBusValue.From(Volume)),
            "Name" => ValueTask.FromResult(DBusValue.From("echo")),
            _ => base.GetPropertyAsync(interfaceName, name, context),
        };

        protected override ValueTask<ImmutableDictionary<string, DBusValue>> GetAllPropertiesAsync(string interfaceName, DBusMethodContext context) =>
            ValueTask.FromResult(ImmutableDictionary<string, DBusValue>.Empty.Add("Volume", DBusValue.From(Volume)).Add("Name", DBusValue.From("echo")));

        protected override ValueTask SetPropertyAsync(string interfaceName, string name, DBusValue value, DBusMethodContext context)
        {
            if (name == "Name") throw new DBusException(DBusException.PropertyReadOnly, "Name is read-only.");
            if (name != "Volume") return base.SetPropertyAsync(interfaceName, name, value, context);
            Volume = value.GetDouble();
            EmitPropertiesChanged(Interface, new Dictionary<string, DBusValue> { ["Volume"] = value });
            return ValueTask.CompletedTask;
        }

        public void Announce(int index)
        {
            _counter = index;
            EmitPropertiesChanged(Interface, new Dictionary<string, DBusValue> { ["Counter"] = DBusValue.From(_counter) }, ["Name"]);
        }

        public bool Ping(string text) => EmitSignal(Interface, "Pinged", DBusValue.From(text));
    }
}
