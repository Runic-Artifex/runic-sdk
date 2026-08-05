using System;
using System.Collections;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RunicToolkit.MVVM;
using RunicToolkit.MVVM.CommunityToolkit;

namespace RunicToolkit.MVVM.CommunityToolkit.AotSmoke;

internal static partial class Program
{
    public static async Task<int> Main()
    {
        var model = new AotViewModel();
        await using CommunityToolkitMvvmBindingAdapter<AotViewModel> adapter = new CommunityToolkitMvvmAdapterBuilder<AotViewModel>(model)
            .BindProperty(1, nameof(AotViewModel.Name), static vm => vm.Name, static (vm, value) => vm.Name = value, AotJsonContext.Default.String, includeValidation: true)
            .BindAsyncCommand(2, nameof(AotViewModel.LoadCommand), static vm => vm.LoadCommand)
            .Build();
        MvvmBindingResult invalid = await adapter.DispatchAsync(Mutation(MvvmMutationKind.SetProperty, 1, "null"), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<MvvmBindingResult> command = adapter.DispatchAsync(Mutation(MvvmMutationKind.ExecuteCommand, 2, "null"), cancellation.Token).AsTask();
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try
        {
            await command.ConfigureAwait(false);
            return Failure();
        }
        catch (OperationCanceledException)
        {
        }

        if (!invalid.Succeeded || !invalid.Patches.Any(static patch => patch is MvvmValidationPatch) || !model.Cancelled)
        {
            return Failure();
        }

        Console.WriteLine("PASS: trimmed CommunityToolkit adapter smoke.");
        return 0;
    }

    private static int Failure()
    {
        Console.Error.WriteLine("FAIL: trimmed CommunityToolkit adapter smoke.");
        return 1;
    }

    private static MvvmMutationRequest Mutation(MvvmMutationKind kind, int memberId, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new MvvmMutationRequest(new MvvmRequestId(Guid.NewGuid()), kind, 0, memberId, document.RootElement);
    }

    internal sealed partial class AotViewModel : ObservableObject, INotifyDataErrorInfo
    {
        [ObservableProperty]
        private string? name = "ready";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Cancelled { get; private set; }

        public bool HasErrors => Name is null;

        public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

        public IEnumerable GetErrors(string? propertyName) =>
            HasErrors && (propertyName is null || propertyName == nameof(Name))
                ? new[] { "The Name field is required." }
                : Array.Empty<string>();

        partial void OnNameChanged(string? value) =>
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(Name)));

        [RelayCommand(FlowExceptionsToTaskScheduler = true)]
        private async Task LoadAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled = true;
                throw;
            }
        }
    }

    [JsonSerializable(typeof(string))]
    private sealed partial class AotJsonContext : JsonSerializerContext
    {
    }
}
