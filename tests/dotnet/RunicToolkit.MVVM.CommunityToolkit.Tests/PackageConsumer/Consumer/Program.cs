using System;
using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RunicToolkit.MVVM;
using RunicToolkit.MVVM.CommunityToolkit;

namespace RunicToolkit.MVVM.CommunityToolkit.PackageConsumer;

internal static partial class Program
{
    public static async Task<int> Main()
    {
        var model = new ConsumerViewModel();
        await using CommunityToolkitMvvmBindingAdapter<ConsumerViewModel> adapter = new CommunityToolkitMvvmAdapterBuilder<ConsumerViewModel>(model)
            .BindProperty(1, nameof(ConsumerViewModel.Name), static vm => vm.Name, static (vm, value) => vm.Name = value, ConsumerJsonContext.Default.String, includeValidation: true)
            .BindAsyncCommand(2, nameof(ConsumerViewModel.LoadCommand), static vm => vm.LoadCommand)
            .Build();

        MvvmBindingResult name = await adapter.DispatchAsync(Mutation(MvvmMutationKind.SetProperty, 1, "null"), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<MvvmBindingResult> command = adapter.DispatchAsync(Mutation(MvvmMutationKind.ExecuteCommand, 2, "null"), cancellation.Token).AsTask();
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try
        {
            await command.ConfigureAwait(false);
            return Fail();
        }
        catch (OperationCanceledException)
        {
        }

        bool noTooling = AppDomain.CurrentDomain.GetAssemblies().All(static assembly =>
            !assembly.GetName().Name!.Contains("RunicToolkit.MVVM.Build", StringComparison.Ordinal)) &&
            !File.Exists(Path.Combine(AppContext.BaseDirectory, "RunicToolkit.MVVM.Build.dll"));
        bool validationPatch = name.Succeeded && name.Patches.Any(static patch => patch is MvvmValidationPatch);
        if (!model.Cancelled || !validationPatch || !noTooling)
        {
            return Fail();
        }

        Console.WriteLine("PASS: packaged CommunityToolkit adapter consumer.");
        return 0;
    }

    private static int Fail()
    {
        Console.Error.WriteLine("FAIL: packaged CommunityToolkit adapter consumer.");
        return 1;
    }

    private static MvvmMutationRequest Mutation(MvvmMutationKind kind, int memberId, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new MvvmMutationRequest(new MvvmRequestId(Guid.NewGuid()), kind, 0, memberId, document.RootElement);
    }

    internal sealed partial class ConsumerViewModel : ObservableObject, INotifyDataErrorInfo
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
    private sealed partial class ConsumerJsonContext : JsonSerializerContext
    {
    }
}
