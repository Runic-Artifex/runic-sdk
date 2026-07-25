using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.ReactiveUI;

namespace WebUIToolkit.MVVM.ReactiveUI.AotSmoke;

internal static partial class Program
{
    public static async Task<int> Main()
    {
        var model = new AotViewModel();
        await using ReactiveUiMvvmBindingAdapter<AotViewModel> adapter =
            new ReactiveUiMvvmAdapterBuilder<AotViewModel>(model)
                .ActivateWith(static viewModel => viewModel.Activator.Activate())
                .BindProperty(
                    1,
                    nameof(AotViewModel.Amount),
                    static viewModel => viewModel.Amount,
                    static (viewModel, value) => viewModel.Amount = value,
                    AotJsonContext.Default.Int32)
                .BindCommand(
                    2,
                    nameof(AotViewModel.SubmitCommand),
                    static viewModel => viewModel.SubmitCommand,
                    AotJsonContext.Default.Int32)
                .Build();
        MvvmBindingResult property = await adapter.DispatchAsync(
            Mutation(MvvmMutationKind.SetProperty, 1, "7"),
            CancellationToken.None);
        MvvmBindingResult command = await adapter.DispatchAsync(
            Mutation(MvvmMutationKind.ExecuteCommand, 2, "null"),
            CancellationToken.None);
        await adapter.DisposeAsync();
        if (!property.Succeeded ||
            !command.Succeeded ||
            command.Payload!.Value.GetInt32() != 1 ||
            model.Amount != 7 ||
            model.ActivationCount != 1 ||
            model.DeactivationCount != 1)
        {
            return 1;
        }

        Console.WriteLine("PASS: ReactiveUI trimmed Native-AOT lifecycle smoke.");
        return 0;
    }

    private static MvvmMutationRequest Mutation(
        MvvmMutationKind kind,
        int memberId,
        string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new MvvmMutationRequest(
            new MvvmRequestId(Guid.NewGuid()),
            kind,
            0,
            memberId,
            document.RootElement);
    }

    [JsonSerializable(typeof(int))]
    private sealed partial class AotJsonContext : JsonSerializerContext
    {
    }

    private sealed partial class AotViewModel : ReactiveObject, IActivatableViewModel
    {
        [Reactive]
        private int _amount;

        public int ActivationCount { get; private set; }
        public int DeactivationCount { get; private set; }
        public int Submissions { get; private set; }
        public ViewModelActivator Activator { get; } = new();

        public AotViewModel()
        {
            this.WhenActivated(disposables =>
            {
                ActivationCount++;
                disposables.Add(Disposable.Create(() => DeactivationCount++));
            });
        }

        [ReactiveCommand]
        private int Submit()
        {
            Submissions++;
            return Submissions;
        }
    }
}
