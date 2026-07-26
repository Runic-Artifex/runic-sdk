using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.CommunityToolkit;

namespace WebUIToolkit.MVVM.CommunityToolkit.Tests;

internal static partial class Program
{
    private static int _passed;

    public static async Task<int> Main()
    {
        try
        {
            await RunAsync("communitytoolkit.observable-property.v1", ObservablePropertyAndValidationAsync);
            await RunAsync("communitytoolkit.relay-command.v1", RelayCommandAndCanExecuteAsync);
            await RunAsync("communitytoolkit.async-command-cancellation.v1", AsyncCommandCancellationAsync);
            await RunAsync("communitytoolkit.validation-metadata.v1", MetadataAndValidationAreDeterministicAsync);
            await RunAsync("communitytoolkit.generated-metadata.v1", MetadataIsOrderedAsync);
            await RunAsync("communitytoolkit.observable-collection.v1", ObservableCollectionProjectionAsync);
            await RunAsync("communitytoolkit.generated-member.title.v1", ExistingTitleProofShapeAsync);
            await RunAsync("communitytoolkit.generated-member.submit-command.v1", ExistingCommandProofShapeAsync);
            await RunAsync("g4-core-vertical.amount-submit.v1", CoreVerticalScenarioAsync);
            await RunCommunityToolkitG3EvidenceAsync();
            Console.WriteLine($"PASS: {_passed} CommunityToolkit conformance fixtures");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL after {_passed} CommunityToolkit fixtures: {exception}");
            return 1;
        }
    }

    private static async Task ObservablePropertyAndValidationAsync()
    {
        var viewModel = new FixtureViewModel();
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter = CreateAdapter(viewModel);
        MvvmSnapshot initial = await adapter.SnapshotAsync(CancellationToken.None);
        Equal(0, ValidationErrors(initial, 1).Length);

        int nameChanges = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FixtureViewModel.Name))
            {
                nameChanges++;
            }
        };

        MvvmBindingResult result = await adapter.DispatchAsync(PropertyMutation(1, Json("null")), CancellationToken.None);
        True(result.Succeeded);
        True(viewModel.Name is null);
        Equal(1, nameChanges);
        Equal(6, result.Patches.Count);
        Equal(MvvmPatchKind.Property, result.Patches[0].Kind);
        Equal(MvvmPatchKind.Validation, result.Patches[1].Kind);
        True(((MvvmValidationPatch)result.Patches[1]).Errors.Count != 0);

        await adapter.DisposeAsync();
        await adapter.DisposeAsync();
        await ThrowsAsync<ObjectDisposedException>(
            async () => await adapter.SnapshotAsync(CancellationToken.None));
    }

    private static async Task RelayCommandAndCanExecuteAsync()
    {
        var viewModel = new FixtureViewModel();
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter = CreateAdapter(viewModel);
        MvvmBindingResult unavailable = await adapter.DispatchAsync(CommandMutation(2, Json("null")), CancellationToken.None);
        False(unavailable.Succeeded);

        MvvmBindingResult enabled = await adapter.DispatchAsync(PropertyMutation(4, Json("true")), CancellationToken.None);
        True(enabled.Succeeded);
        MvvmBindingResult submitted = await adapter.DispatchAsync(CommandMutation(2, Json("null")), CancellationToken.None);
        True(submitted.Succeeded);
        Equal(1, viewModel.SubmissionCount);
        True(viewModel.Name is null);
        True(submitted.Patches.Any(static patch =>
            patch is MvvmPropertyPatch { MemberId: 1 }));
        True(submitted.Patches.Any(static patch =>
            patch is MvvmValidationPatch { MemberId: 1 } validation &&
            validation.Errors.Count != 0));
        MvvmBindingResult multiplied = await adapter.DispatchAsync(CommandMutation(3, Json("6")), CancellationToken.None);
        True(multiplied.Succeeded);
        Equal(6, viewModel.MultipliedBy);
    }

    private static async Task AsyncCommandCancellationAsync()
    {
        var viewModel = new FixtureViewModel();
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter = CreateAdapter(viewModel);
        using var cancellation = new CancellationTokenSource();
        Task<MvvmBindingResult> operation = adapter
            .DispatchAsync(CommandMutation(5, Json("null")), cancellation.Token)
            .AsTask();
        await viewModel.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () => await operation);
        True(viewModel.Cancelled);
    }

    private static async Task MetadataAndValidationAreDeterministicAsync()
    {
        var viewModel = new FixtureViewModel();
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> first = CreateAdapter(viewModel);
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> second = CreateAdapter(viewModel);
        Equal(
            string.Join("|", first.Metadata.Select(static entry => $"{entry.MemberId}:{entry.Kind}:{entry.GeneratedMemberName}")),
            string.Join("|", second.Metadata.Select(static entry => $"{entry.MemberId}:{entry.Kind}:{entry.GeneratedMemberName}")));
    }

    private static async Task MetadataIsOrderedAsync()
    {
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter = CreateAdapter(new FixtureViewModel());
        Equal("1,2,3,4,5,6,7", string.Join(',', adapter.Metadata.Select(static entry => entry.MemberId)));
        Equal("Name", adapter.Metadata[0].GeneratedMemberName);
        Equal(MvvmBindingMemberKind.Collection, adapter.Metadata[5].Kind);
        Equal(MvvmBindingMemberKind.Command, adapter.Metadata[6].Kind);
    }

    private static async Task ObservableCollectionProjectionAsync()
    {
        var viewModel = new FixtureViewModel();
        viewModel.Items.Add("first");
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter = CreateAdapter(viewModel);

        MvvmSnapshot initial = await adapter.SnapshotAsync(CancellationToken.None);
        JsonElement collection = initial.State.GetProperty("members").EnumerateArray()
            .Single(element =>
                element.GetProperty("type").GetString() == "collection" &&
                element.GetProperty("member").GetInt32() == 6);
        Equal("first", collection.GetProperty("items")[0].GetString()!);

        MvvmBindingResult added = await adapter.DispatchAsync(
            CommandMutation(7, Json("\"second\"")),
            CancellationToken.None);
        True(added.Succeeded);
        MvvmCollectionPatch reset = added.Patches
            .OfType<MvvmCollectionPatch>()
            .Single(static patch => patch.MemberId == 6);
        Equal(MvvmCollectionOperation.Reset, reset.Operation);
        Equal(2, reset.Items.Count);
        Equal("second", reset.Items[1].GetString()!);
    }

    private static async Task ExistingTitleProofShapeAsync()
    {
        var viewModel = new FixtureViewModel { Name = "before" };
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter = CreateAdapter(viewModel);
        MvvmBindingResult result = await adapter.DispatchAsync(PropertyMutation(1, Json("\"after\"")), CancellationToken.None);
        True(result.Succeeded);
        Equal("after", viewModel.Name);
    }

    private static async Task ExistingCommandProofShapeAsync()
    {
        var viewModel = new FixtureViewModel { CanSubmit = true };
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter = CreateAdapter(viewModel);
        MvvmBindingResult result = await adapter.DispatchAsync(CommandMutation(2, Json("null")), CancellationToken.None);
        True(result.Succeeded);
        Equal(1, viewModel.SubmissionCount);
    }

    private static CommunityToolkitMvvmBindingAdapter<FixtureViewModel> CreateAdapter(FixtureViewModel viewModel) =>
        new CommunityToolkitMvvmAdapterBuilder<FixtureViewModel>(viewModel)
            .BindProperty(1, nameof(FixtureViewModel.Name), static model => model.Name, static (model, value) => model.Name = value, FixtureJsonContext.Default.String, includeValidation: true)
            .BindCommand(2, nameof(FixtureViewModel.SubmitCommand), static model => model.SubmitCommand)
            .BindCommand(3, nameof(FixtureViewModel.MultiplyCommand), static model => model.MultiplyCommand, FixtureJsonContext.Default.Int32)
            .BindProperty(4, nameof(FixtureViewModel.CanSubmit), static model => model.CanSubmit, static (model, value) => model.CanSubmit = value, FixtureJsonContext.Default.Boolean)
            .BindAsyncCommand(5, nameof(FixtureViewModel.LoadCommand), static model => model.LoadCommand)
            .BindCollection(6, nameof(FixtureViewModel.Items), static model => model.Items, FixtureJsonContext.Default.String)
            .BindCommand(7, nameof(FixtureViewModel.AddItemCommand), static model => model.AddItemCommand, FixtureJsonContext.Default.String)
            .Build();

    private static MvvmMutationRequest PropertyMutation(int memberId, JsonElement payload) =>
        new(new MvvmRequestId(Guid.NewGuid()), MvvmMutationKind.SetProperty, 0, memberId, payload);

    private static MvvmMutationRequest CommandMutation(int memberId, JsonElement payload) =>
        new(new MvvmRequestId(Guid.NewGuid()), MvvmMutationKind.ExecuteCommand, 0, memberId, payload);

    private static JsonElement Json(string value)
    {
        using JsonDocument document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string[] ValidationErrors(MvvmSnapshot snapshot, int memberId)
    {
        JsonElement member = snapshot.State.GetProperty("members").EnumerateArray()
            .Single(element => element.GetProperty("type").GetString() == "validation" && element.GetProperty("member").GetInt32() == memberId);
        return member.GetProperty("errors").EnumerateArray().Select(static error => error.GetString()!).ToArray();
    }

    private static async Task RunAsync(string fixtureId, Func<Task> test)
    {
        await test().ConfigureAwait(false);
        _passed++;
        Console.WriteLine($"PASS: {fixtureId}");
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool value) => True(!value);

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    internal sealed partial class FixtureViewModel : ObservableValidator
    {
        [ObservableProperty]
        [NotifyDataErrorInfo]
        [Required]
        private string? name = "initial";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
        private bool canSubmit;

        public int SubmissionCount { get; private set; }

        public int MultipliedBy { get; private set; }

        public bool Cancelled { get; private set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ObservableCollection<string> Items { get; } = [];

        [RelayCommand(CanExecute = nameof(CanSubmit))]
        private void Submit()
        {
            SubmissionCount++;
            Name = null;
        }

        [RelayCommand]
        private void Multiply(int factor) => MultipliedBy = factor;

        [RelayCommand]
        private void AddItem(string value) => Items.Add(value);

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
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(bool))]
    private sealed partial class FixtureJsonContext : JsonSerializerContext
    {
    }
}
