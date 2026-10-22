using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

    [Test, Explicit, Category("DBus"), DisplayName("disposing a subscription closes the descriptors of signals nobody read")]
    public async Task UnreadDescriptorsAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        var echo = new EchoObject();
        await using var export = service.Export(ObjectPath, echo);
        var proxy = new DBusProxy(client, service.UniqueName, ObjectPath);
        // Disposed before reading, and disposed after its enumeration stopped with a signal left unread.
        foreach (bool enumerate in new[] { false, true })
        {
            var handed = await proxy.SubscribeSignalAsync(Interface, "Handed");
            await using var marks = await proxy.SubscribeSignalAsync(Interface, "Pinged");
            using var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In);
            Check(echo.Hand(pipe.ClientSafePipeHandle) && echo.Hand(pipe.ClientSafePipeHandle) && echo.Ping("after"), "emitting");
            pipe.DisposeLocalCopyOfClientHandle();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // The connection dispatches messages in order, so both descriptors are buffered once the mark arrives.
            await using (var mark = marks.GetAsyncEnumerator(timeout.Token)) Check(await mark.MoveNextAsync(), "the mark arrived");
            if (enumerate)
            {
                await using var reader = handed.GetAsyncEnumerator(timeout.Token);
                Check(await reader.MoveNextAsync(), "the first descriptor arrived");
                reader.Current.Arguments[0].GetUnixFd().Dispose();
            }
            await handed.DisposeAsync();
            // The pipe reports end of file only when every copy of its write end is closed.
            int read = await pipe.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Check(read == 0, "the pipe closed");
        }
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

    [Test, Explicit, Category("DBus"), DisplayName("a call that outlives its timeout fails with NoReply, from a call or a proxy")]
    public async Task TimeoutAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        await using var export = service.Export(ObjectPath, new EchoObject());
        var started = Stopwatch.StartNew();
        var call = new DBusMethodCall(service.UniqueName, ObjectPath, Interface, "Sleep") { Arguments = [DBusValue.From(5000)], Timeout = TimeSpan.FromMilliseconds(200) };
        Check(await ErrorAsync(() => client.CallMethodAsync(call)) is { ErrorName: DBusException.NoReply }, "the call times out with NoReply");
        Check(started.Elapsed < TimeSpan.FromSeconds(4), $"the timeout ended the wait ({started.Elapsed})");
        var proxy = new DBusProxy(client, service.UniqueName, ObjectPath) { Timeout = TimeSpan.FromMilliseconds(200) };
        Check(await ErrorAsync(() => proxy.CallAsync(Interface, "Sleep", [DBusValue.From(5000)])) is { ErrorName: DBusException.NoReply }, "the proxy's timeout applies");
        Check((await proxy.CallAsync(Interface, "Echo", [DBusValue.From(1)])) is [{ } one] && one.GetInt32() == 1, "a prompt reply arrives within the timeout");
        Check((await proxy.GetPropertyAsync(Interface, "Volume")).GetDouble() == 0.5, "property reads go through the proxy's timeout too");
        Check(await ErrorAsync(() => client.CallMethodAsync(call with { Timeout = System.Threading.Timeout.InfiniteTimeSpan, Arguments = [DBusValue.From(50)] })) is null,
            "an infinite timeout waits");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await ThrowsAsync<OperationCanceledException>(() => client.CallMethodAsync(call with { Timeout = TimeSpan.FromSeconds(30) }, cancel.Token).AsTask(),
            "cancelling the token still cancels before the timeout");
    }

    [Test, Explicit, Category("DBus"), DisplayName("argument keys of a signal filter deliver only the signals they match")]
    public async Task ArgumentFiltersAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        var echo = new EchoObject();
        await using var export = service.Export(ObjectPath, echo);
        DBusSignalFilter Tagged() => new() { Sender = service.UniqueName, Path = ObjectPath, Interface = Interface, Member = "Tagged" };
        await using var second = await client.SubscribeAsync(Tagged() with { Arguments = ImmutableDictionary<int, string>.Empty.Add(1, "keep") });
        await using var path = await client.SubscribeAsync(Tagged() with { ArgumentPaths = ImmutableDictionary<int, string>.Empty.Add(2, "/org/runic/") });
        await using var space = await client.SubscribeAsync(Tagged() with { Arg0Namespace = "org.runic" });
        await using var first = await client.SubscribeAsync(Tagged() with { ArgumentPaths = ImmutableDictionary<int, string>.Empty.Add(0, "/org/runic/") });
        await using var all = await client.SubscribeAsync(Tagged());
        static DBusValue[] Args(string name, string tag, string objectPath) => [DBusValue.From(name), DBusValue.From(tag), DBusValue.From(new DBusObjectPath(objectPath))];
        Check(echo.Tag(Args("org.runic.Player", "keep", "/org/runic/a")) && echo.Tag(Args("org.runicx", "drop", "/org/other"))
            && echo.Tag(Args("org.runic", "drop", "/org/runicx")) && echo.Tag(Args("/org/runic/a/b", "keep", "/org")) && echo.Tag([DBusValue.From(5)]), "emitting");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Every signal reached the unfiltered subscription; the connection dispatches in order, so the others have theirs.
        await using (var marks = all.GetAsyncEnumerator(timeout.Token))
            for (int index = 0; index < 5; index++) Check(await marks.MoveNextAsync(), $"signal {index} arrived");
        // What each subscription received is already buffered: read until it has nothing more for a moment.
        static async Task<List<string>> FirstArgumentsAsync(DBusSignalSubscription subscription)
        {
            var seen = new List<string>();
            using var idle = new CancellationTokenSource();
            await using var signals = subscription.GetAsyncEnumerator(idle.Token);
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromMilliseconds(200));
                try { if (!await signals.MoveNextAsync()) break; }
                catch (OperationCanceledException) { break; }
                var first = signals.Current.Arguments[0];
                seen.Add(first.Signature.Value == "s" ? first.GetString() : first.ToString());
            }
            return seen;
        }
        Check((await FirstArgumentsAsync(second)).SequenceEqual(["org.runic.Player", "/org/runic/a/b"]), "arg1 matches the string");
        // /org/runic/ matches /org/runic/a below it, but not /org/runicx, /org/other, or /org, which does not end in '/'.
        Check((await FirstArgumentsAsync(path)).SequenceEqual(["org.runic.Player"]), "arg2path matches paths below the prefix");
        Check((await FirstArgumentsAsync(space)).SequenceEqual(["org.runic.Player", "org.runic"]), "arg0namespace matches the name and names below it");
        Check((await FirstArgumentsAsync(first)).SequenceEqual(["/org/runic/a/b"]), "arg0path matches a string argument below the prefix");
    }

    [Test, Explicit, Category("DBus"), DisplayName("a filter with an argument position outside 0-63, conflicting arg0 keys or a bad namespace is refused")]
    public async Task ArgumentFilterValidationAsync()
    {
        await using var client = await ConnectAsync();
        await ThrowsAsync<ArgumentException>(() => client.SubscribeAsync(new DBusSignalFilter { Arguments = ImmutableDictionary<int, string>.Empty.Add(64, "x") }).AsTask(), "position 64");
        await ThrowsAsync<ArgumentException>(() => client.SubscribeAsync(new DBusSignalFilter { ArgumentPaths = ImmutableDictionary<int, string>.Empty.Add(-1, "/") }).AsTask(), "position -1");
        await ThrowsAsync<ArgumentException>(() => client.SubscribeAsync(new DBusSignalFilter { Arg0 = "a", Arguments = ImmutableDictionary<int, string>.Empty.Add(0, "b") }).AsTask(),
            "different arg0 values");
        await ThrowsAsync<ArgumentException>(() => client.SubscribeAsync(new DBusSignalFilter { Arg0Namespace = "org..runic" }).AsTask(), "a bad namespace");
        await using var same = await client.SubscribeAsync(new DBusSignalFilter { Arg0 = "a", Arguments = ImmutableDictionary<int, string>.Empty.Add(0, "a") });
        await using var single = await client.SubscribeAsync(new DBusSignalFilter { Arg0Namespace = "org" });
    }

    [Test, Explicit, Category("DBus"), DisplayName("a large ay crosses the bus as one block of bytes")]
    public async Task LargeByteArrayAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        await using var export = service.Export(ObjectPath, new EchoObject());
        var bytes = new byte[4 * 1024 * 1024];
        new Random(20).NextBytes(bytes);
        var reply = await new DBusProxy(client, service.UniqueName, ObjectPath).CallAsync(Interface, "Echo",
            [DBusValue.ByteArray(bytes), DBusValue.Array("ay", [DBusValue.ByteArray([1]), DBusValue.ByteArray([])])]);
        Check(reply is [var echoed, var nested] && echoed.GetByteArray().AsSpan().SequenceEqual(bytes) && nested.GetItems()[0].GetByteArray() is [1]
            && nested.GetItems()[1].GetByteArray().IsEmpty, "the bytes and nested byte arrays came back");
    }

    [Test, Explicit, Category("DBus"), DisplayName("the ObjectManager proxy reads the managed objects and their interface changes")]
    public async Task ObjectManagerAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        var manager = new ManagerObject();
        await using var export = service.Export("/org/runic/Test", manager);
        var proxy = new DBusObjectManagerProxy(client, service.UniqueName, "/org/runic/Test");
        await using var changes = await proxy.SubscribeInterfaceChangesAsync();
        var objects = await proxy.GetManagedObjectsAsync();
        Check(objects.Count == 1 && objects["/org/runic/Test/Disk1"]["org.runic.Test.Disk"]["Size"].GetUInt64() == 42
            && objects["/org/runic/Test/Disk1"]["org.runic.Test.Disk"]["Label"].GetString() == "data", "the objects with unwrapped properties");
        Check(manager.Add("/org/runic/Test/Disk2", "org.runic.Test.Disk", 7) && manager.Remove("/org/runic/Test/Disk1", "org.runic.Test.Disk", "org.runic.Test.Partition"),
            "emitting");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var signals = changes.GetAsyncEnumerator(timeout.Token);
        Check(await signals.MoveNextAsync() && DBusObjectManagerProxy.ReadInterfaceChanges(signals.Current) is { } added
            && added.Path.Value == "/org/runic/Test/Disk2" && added.Added["org.runic.Test.Disk"]["Size"].GetUInt64() == 7 && added.Removed.IsEmpty, "InterfacesAdded");
        Check(await signals.MoveNextAsync() && DBusObjectManagerProxy.ReadInterfaceChanges(signals.Current) is { } removed
            && removed.Path.Value == "/org/runic/Test/Disk1" && removed.Added.IsEmpty && removed.Removed.SequenceEqual(["org.runic.Test.Disk", "org.runic.Test.Partition"]),
            "InterfacesRemoved");
        await using var watch = proxy.WatchInterfaceChangesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var first = watch.MoveNextAsync();
        // The watch subscribes when enumeration starts; emit until it reports one.
        while (!first.IsCompleted) { manager.Add("/org/runic/Test/Disk3", "org.runic.Test.Disk", 3); await Task.WhenAny(first.AsTask(), Task.Delay(50, timeout.Token)); }
        Check(await first && watch.Current.Path.Value == "/org/runic/Test/Disk3", "WatchInterfaceChangesAsync reports changes");
        var plain = new DBusObjectManagerProxy(client, service.UniqueName, ObjectPath);
        var error = await ErrorAsync(() => plain.GetManagedObjectsAsync());
        Check(error is { ErrorName: DBusException.UnknownMethod }, $"a path without a manager replies with an error: {error?.ErrorName}");
    }

    [Test, Explicit, Category("DBus"), DisplayName("an object runs at most MaxConcurrentCalls calls, starts them in order and refuses a flood with LimitsExceeded")]
    public async Task CallLimitAsync()
    {
        await using var service = await ConnectAsync();
        await using var client = await ConnectAsync();
        Throws<InvalidOperationException>(() => service.Export("/org/runic/Test/None", new HoldObject(0)), "a limit below 1");

        // One at a time: the calls queue in arrival order, and one past the waiting room is refused at once.
        var serial = new HoldObject(1);
        await using (service.Export(ObjectPath, serial))
        {
            var proxy = new DBusProxy(client, service.UniqueName, ObjectPath);
            var calls = Enumerable.Range(0, 1 + 64).Select(index => proxy.CallAsync(Interface, "Hold", [DBusValue.From(index)]).AsTask()).ToArray();
            Check(await ErrorAsync(() => proxy.CallAsync(Interface, "Hold", [DBusValue.From(-1)])) is { ErrorName: DBusException.LimitsExceeded },
                "a call beyond the running and waiting calls is refused");
            await serial.WaitStartedAsync(1);
            await Task.Delay(200);
            Check(serial.Started.Count == 1, $"one call runs at a time: {serial.Started.Count}");
            Check(await ErrorAsync(() => proxy.GetPropertyAsync(Interface, "Name")) is { ErrorName: DBusException.LimitsExceeded },
                "property calls count against the limit too");
            serial.Release(1 + 64);
            await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
            Check(serial.Started.SequenceEqual(Enumerable.Range(0, 1 + 64)), "the calls ran in arrival order");
            Check(serial.MostAtOnce == 1, $"never more than one call at once: {serial.MostAtOnce}");
            serial.Release(1);
            Check((await proxy.CallAsync(Interface, "Hold", [DBusValue.From(99)]))[0].GetInt32() == 99, "the object answers again once its calls finished");
        }

        // The default runs 16 calls at once; the 17th waits for one of them.
        var parallel = new HoldObject(null);
        await using (service.Export(ObjectPath, parallel))
        {
            var proxy = new DBusProxy(client, service.UniqueName, ObjectPath);
            var calls = Enumerable.Range(0, 17).Select(index => proxy.CallAsync(Interface, "Hold", [DBusValue.From(index)]).AsTask()).ToArray();
            await parallel.WaitStartedAsync(16);
            await Task.Delay(200);
            Check(parallel.Started.Count == 16 && !parallel.Started.Contains(16), $"16 calls run at once by default: {parallel.Started.Count}");
            parallel.Release(1);
            await parallel.WaitStartedAsync(17);
            parallel.Release(16);
            await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
            Check(parallel.MostAtOnce == 16, $"at most 16 at once: {parallel.MostAtOnce}");
        }
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

        public bool Hand(SafeHandle descriptor) => EmitSignal(Interface, "Handed", DBusValue.UnixFd(descriptor));

        public bool Tag(DBusValue[] arguments) => EmitSignal(Interface, "Tagged", arguments);
    }

    // Holds each call until released, and records the order and overlap of the calls it ran.
    private sealed class HoldObject(int? limit) : DBusObject
    {
        private readonly System.Threading.Channels.Channel<int> _release = System.Threading.Channels.Channel.CreateUnbounded<int>();
        private readonly Lock _gate = new();
        private int _running;
        public List<int> Started { get; } = [];
        public int MostAtOnce { get; private set; }

        protected override int MaxConcurrentCalls => limit ?? base.MaxConcurrentCalls;

        public void Release(int count)
        {
            for (int index = 0; index < count; index++) _release.Writer.TryWrite(index);
        }

        public async Task WaitStartedAsync(int count)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                lock (_gate) if (Started.Count >= count) return;
                await Task.Delay(10, deadline.Token);
            }
        }

        protected override async ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context)
        {
            lock (_gate)
            {
                Started.Add(context.Arguments[0].GetInt32());
                MostAtOnce = Math.Max(MostAtOnce, ++_running);
            }
            try { await _release.Reader.ReadAsync(context.RequestAborted); }
            finally { lock (_gate) _running--; }
            return [context.Arguments[0]];
        }

        protected override ValueTask<DBusValue> GetPropertyAsync(string interfaceName, string name, DBusMethodContext context) =>
            ValueTask.FromResult(DBusValue.From("hold"));
    }

    // An org.freedesktop.DBus.ObjectManager with one disk to start with.
    private sealed class ManagerObject : DBusObject
    {
        protected override ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context)
        {
            if (context is not { Interface: DBusObjectManagerProxy.InterfaceName, Member: "GetManagedObjects" })
                throw new DBusException(DBusException.UnknownMethod, context.Member);
            var disk = Interfaces("org.runic.Test.Disk", DBusValue.VariantDictionary([KeyValuePair.Create("Size", DBusValue.From(42UL)),
                KeyValuePair.Create("Label", DBusValue.From("data"))]));
            return ValueTask.FromResult<ImmutableArray<DBusValue>>(
                [DBusValue.Dictionary("o", "a{sa{sv}}", [KeyValuePair.Create(DBusValue.From(new DBusObjectPath("/org/runic/Test/Disk1")), disk)])]);
        }

        private static DBusValue Interfaces(string name, DBusValue properties) =>
            DBusValue.Dictionary("s", "a{sv}", [KeyValuePair.Create(DBusValue.From(name), properties)]);

        public bool Add(string path, string name, ulong size) => EmitSignal(DBusObjectManagerProxy.InterfaceName, "InterfacesAdded",
            DBusValue.From(new DBusObjectPath(path)), Interfaces(name, DBusValue.VariantDictionary([KeyValuePair.Create("Size", DBusValue.From(size))])));

        public bool Remove(string path, params string[] names) => EmitSignal(DBusObjectManagerProxy.InterfaceName, "InterfacesRemoved",
            DBusValue.From(new DBusObjectPath(path)), DBusValue.StringArray(names));
    }
}
