using System.Reflection;
using Runic.Application.Views.Generated;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// S4 of #1109: each generated Bridge describes its members as a table, which the assembly's generated
// RunicBridgeTable reads once per ViewModel type (RunicShared.Tables.Bridge.g.cs).
internal sealed class BridgeTableTests
{
    // Every generated Bridge of this test assembly: reading its Table runs the reader on its records.
    private static IEnumerable<(Type Bridge, object Table)> Tables() =>
        typeof(BridgeTableTests).Assembly.GetTypes()
            .Where(type => type.Namespace == "Runic.Application.Views.Generated.Bridges" && typeof(ViewModelBridge).IsAssignableFrom(type))
            .Select(type => (type, type.GetField("Table", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!));

    private static CommandDescriptor[] Commands(object table) =>
        (CommandDescriptor[])table.GetType().GetProperty("Commands", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(table)!;

    [Test]
    public void EveryGeneratedTableReads()
    {
        var tables = Tables().ToArray();
        Require(tables.Length > 100, $"Only {tables.Length} generated Bridges were found.");
        foreach (var (bridge, table) in tables)
            Require(table.GetType().Name == "RunicBridgeTable", $"{bridge.Name} has no descriptor table.");
    }

    // A stream command's ExecuteStreamAsync and an async key resolver's ResolveArgumentAsync were async lambdas, and the
    // runtime calls them without awaiting first (BridgeRuntime.Operations): an exception, even one before their first
    // await, must stay the returned task's. Their arms are in async switch methods, so it does. A ViewModel of another
    // type makes the arm's cast throw before anything else runs.
    [Test]
    public async Task StreamCommandsAndAsyncKeyResolversFailThroughTheirTaskAsync()
    {
        var streams = 0;
        var resolvers = 0;
        foreach (var (bridge, table) in Tables())
        {
            foreach (var command in Commands(table))
            {
                if (command.ExecuteStreamAsync is { } executeStream)
                {
                    streams++;
                    var task = executeStream(new object(), new BridgeOperationExecution(command.CreateStream!()), CancellationToken.None, null);
                    await RequireFaultedAsync(task, $"{bridge.Name}.{command.Name} ExecuteStreamAsync").ConfigureAwait(false);
                }
                if (command.ResolveArgumentAsync is { } resolve)
                {
                    resolvers++;
                    var task = resolve(new object(), null, CancellationToken.None).AsTask();
                    await RequireFaultedAsync(task, $"{bridge.Name}.{command.Name} ResolveArgumentAsync").ConfigureAwait(false);
                }
            }
        }
        Require(streams > 0 && resolvers > 0, $"The fixtures have {streams} stream commands and {resolvers} async key resolvers.");
    }

    private static async Task RequireFaultedAsync(Task task, string member)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (InvalidCastException)
        {
            return;
        }
        throw new InvalidOperationException($"{member} did not fail with its ViewModel's cast.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
