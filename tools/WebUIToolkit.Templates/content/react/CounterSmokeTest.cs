using System;
using System.Text.Json;
using System.Threading.Tasks;
using WebUIToolkit.MVVM;

namespace WebUIToolkitStarter;

internal static class CounterSmokeTest
{
    internal static async Task<int> RunAsync()
    {
        var model = new CounterViewModel();
        var registry = new MvvmSessionRegistry();
        var contract = new MvvmContract(CounterContracts.Counter.Name);
        registry.Map(contract, _ => ValueTask.FromResult(
            new MvvmSessionActivation(CounterContracts.Counter.CreateAdapter(model))));
        await using IMvvmSessionFactory sessions = registry.Build();
        await using IMvvmSession session = await sessions.OpenAsync(contract);
        MvvmResponse snapshot = await session.DispatchAsync(
            new MvvmSnapshotRequest(new MvvmRequestId(Guid.NewGuid())));
        MvvmResponse changed = await session.DispatchAsync(new MvvmMutationRequest(
            new MvvmRequestId(Guid.NewGuid()),
            MvvmMutationKind.SetProperty,
            session.Revision,
            CounterContracts.Counter.Members.Step,
            JsonSerializer.SerializeToElement(2, CounterJsonContext.Default.Int32)));
        using JsonDocument none = JsonDocument.Parse("null");
        MvvmResponse incremented = await session.DispatchAsync(new MvvmMutationRequest(
            new MvvmRequestId(Guid.NewGuid()),
            MvvmMutationKind.ExecuteCommand,
            session.Revision,
            CounterContracts.Counter.Members.Increment,
            none.RootElement));
        bool passed = snapshot.Succeeded && changed.Succeeded &&
            incremented.Succeeded && model.Count == 2 && model.History.Count == 2;
        Console.WriteLine(passed
            ? "Native counter property, validation, collection, and command smoke test passed."
            : "Native counter smoke test failed.");
        return passed ? 0 : 1;
    }
}
