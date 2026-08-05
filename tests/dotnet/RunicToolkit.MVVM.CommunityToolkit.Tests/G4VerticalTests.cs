using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RunicToolkit.MVVM;
using RunicToolkit.MVVM.CommunityToolkit;

namespace RunicToolkit.MVVM.CommunityToolkit.Tests;

internal static partial class Program
{
    private static async Task CoreVerticalScenarioAsync()
    {
        var viewModel = new CoreVerticalViewModel();
        await using CommunityToolkitMvvmBindingAdapter<CoreVerticalViewModel> adapter =
            new CommunityToolkitMvvmAdapterBuilder<CoreVerticalViewModel>(viewModel)
                .BindProperty(
                    1,
                    nameof(CoreVerticalViewModel.Amount),
                    static model => model.Amount,
                    static (model, value) => model.Amount = value,
                    FixtureJsonContext.Default.Int32)
                .BindCommand(
                    2,
                    nameof(CoreVerticalViewModel.SubmitCommand),
                    static model => model.SubmitCommand)
                .Build();

        MvvmBindingResult property = await adapter.DispatchAsync(
            PropertyMutation(1, Json("7")),
            CancellationToken.None);
        MvvmBindingResult command = await adapter.DispatchAsync(
            CommandMutation(2, Json("null")),
            CancellationToken.None);

        True(property.Succeeded && property.Committed);
        True(command.Succeeded && command.Committed);
        Equal(7, viewModel.Amount);
        Equal(1, viewModel.Submissions);
        Console.WriteLine(
            "G4-VERTICAL: communitytoolkit/amount-submit-v1 amount=7 submissions=1 commits=2");
    }

    private sealed partial class CoreVerticalViewModel : ObservableObject
    {
        [ObservableProperty]
        private int amount;

        public int Submissions { get; private set; }

        [RelayCommand]
        private void Submit() => Submissions++;
    }
}
