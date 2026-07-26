using System;
using System.Text.Json;
using System.Threading.Tasks;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.CommunityToolkit;

namespace WebUIToolkit.Samples.MvvmCounterProjection;

internal static class Program
{
    private const int CountMember = 1;
    private const int IncrementMember = 2;
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static async Task Main()
    {
        var contract = new MvvmContract("samples.counter");
        var registry = new MvvmSessionRegistry();
        registry.Map(contract, static _ =>
        {
            var viewModel = new CounterViewModel();
            CommunityToolkitMvvmBindingAdapter<CounterViewModel> adapter =
                new CommunityToolkitMvvmAdapterBuilder<CounterViewModel>(viewModel)
                    .BindProperty(
                        CountMember,
                        nameof(CounterViewModel.Count),
                        static model => model.Count,
                        static (model, value) => model.Count = value,
                        CounterJsonContext.Default.Int32)
                    .BindCommand(
                        IncrementMember,
                        nameof(CounterViewModel.IncrementCommand),
                        static model => model.IncrementCommand)
                    .Build();
            return ValueTask.FromResult(new MvvmSessionActivation(adapter));
        });

        await using IMvvmSessionFactory sessions = registry.Build();
        await using IMvvmSession session = await sessions.OpenAsync(contract);

        Console.WriteLine("A browser asks for the initial counter state:");
        MvvmResponse snapshot = await session.DispatchAsync(
            new MvvmSnapshotRequest(NewRequestId()));
        Console.WriteLine(Pretty(snapshot.Payload!.Value));

        Console.WriteLine();
        Console.WriteLine("The user edits the counter to 5:");
        MvvmResponse edited = await session.DispatchAsync(new MvvmMutationRequest(
            NewRequestId(),
            MvvmMutationKind.SetProperty,
            session.Revision,
            CountMember,
            JsonSerializer.SerializeToElement(5, CounterJsonContext.Default.Int32)));
        PrintChanges(edited);

        Console.WriteLine();
        Console.WriteLine("The user presses the increment command:");
        using JsonDocument nullPayload = JsonDocument.Parse("null");
        MvvmResponse incremented = await session.DispatchAsync(new MvvmMutationRequest(
            NewRequestId(),
            MvvmMutationKind.ExecuteCommand,
            session.Revision,
            IncrementMember,
            nullPayload.RootElement));
        PrintChanges(incremented);
    }

    private static MvvmRequestId NewRequestId() => new(Guid.NewGuid());

    private static string Pretty(JsonElement value) =>
        JsonSerializer.Serialize(value, IndentedJson);

    private static void PrintChanges(MvvmResponse response)
    {
        Console.WriteLine($"Revision {response.Revision} contains {response.Patches.Count} UI changes:");
        foreach (MvvmPatch patch in response.Patches)
        {
            string description = patch switch
            {
                MvvmPropertyPatch property =>
                    $"property #{property.MemberId} is now {property.Value}",
                MvvmCommandPatch command =>
                    $"command #{command.MemberId} canExecute={command.CanExecute}",
                _ => $"{patch.Kind} member #{patch.MemberId}",
            };
            Console.WriteLine($"  • {description}");
        }
    }
}
