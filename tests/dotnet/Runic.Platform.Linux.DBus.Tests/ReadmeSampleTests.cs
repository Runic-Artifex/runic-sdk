using System.Collections.Immutable;
using Runic.Platform;
using Runic.Platform.Linux.DBus;
using TUnit.Core;
using static ValueTests;

// The service sample in the package README, kept compiling and working.
internal sealed class ReadmeSampleTests
{
    [Test, Explicit, Category("DBus"), DisplayName("README: the Counter service answers, reports its property and announces changes")]
    public async Task CounterAsync()
    {
        await using var bus = ((PlatformResult<DBusConnection>.Success)await DBusConnection.ConnectAsync(DBusBus.Session)).Value;
        await using var client = ((PlatformResult<DBusConnection>.Success)await DBusConnection.ConnectAsync(DBusBus.Session)).Value;
        await using var export = bus.Export("/org/example/Counter", new Counter());
        Check(await bus.RequestNameAsync("org.example.Counter", DBusRequestNameOptions.DoNotQueue) == DBusRequestNameReply.PrimaryOwner, "name");

        var counter = new DBusProxy(client, "org.example.Counter", "/org/example/Counter");
        await using var changes = await counter.SubscribePropertiesAsync("org.example.Counter");
        Check((await counter.CallAsync("org.example.Counter", "Add", [DBusValue.From(5)]))[0].GetInt32() == 5, "Add");
        Check((await counter.GetPropertyAsync("org.example.Counter", "Value")).GetInt32() == 5, "Value");
        await using var enumerator = changes.GetAsyncEnumerator();
        Check(await enumerator.MoveNextAsync() && DBusProxy.ReadPropertyChanges(enumerator.Current)?.Changed["Value"].GetInt32() == 5, "PropertiesChanged");
        try { await counter.CallAsync("org.example.Counter", "Add", [DBusValue.From("five")]); throw new InvalidOperationException("A wrong signature was accepted."); }
        catch (DBusException error) when (error.ErrorName == DBusException.UnknownMethod) { }
    }

    private sealed class Counter : DBusObject
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
}
