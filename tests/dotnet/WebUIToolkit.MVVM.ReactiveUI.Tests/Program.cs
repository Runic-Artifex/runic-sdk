using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.Build.Symbols;
using WebUIToolkit.MVVM.ReactiveUI;

namespace WebUIToolkit.MVVM.ReactiveUI.Tests;

internal static partial class Program
{
    private static int _passed;

    public static async Task<int> Main()
    {
        try
        {
            await RunAsync("reactiveui.generated-member.visibility.v1", GeneratedMemberVisibilityAsync);
            await RunAsync("reactiveui.property-command-result.v1", PropertyCommandAndResultAsync);
            await RunAsync("reactiveui.activation-scheduler-disposal.v1", ActivationSchedulerAndDisposalAsync);
            await RunAsync("reactiveui.command-fault-routing.v1", CommandFaultRoutingAsync);
            await RunAsync("reactiveui.vertical.amount-submit.v1", VerticalAsync);
            Console.WriteLine($"PASS: {_passed} ReactiveUI G6 fixtures");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL after {_passed} ReactiveUI G6 fixtures: {exception}");
            return 1;
        }
    }

    private static Task GeneratedMemberVisibilityAsync()
    {
        var model = new GeneratedReactiveViewModel();
        model.Amount = 4;
        Equal(4, model.Amount);
        True(model.SubmitCommand is not null);

        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        var request = new PostGeneratorSemanticRequest(
            PostGeneratorSemanticContract.SchemaVersion,
            assemblyPath,
            typeof(GeneratedReactiveViewModel).FullName!,
            new PostGeneratorAdapterCapabilities(
                "webuitoolkit.mvvm.reactiveui/1",
                1,
                PostGeneratorSemanticCapabilities.PropertyGet |
                    PostGeneratorSemanticCapabilities.PropertySet |
                    PostGeneratorSemanticCapabilities.SourceGeneratedSerializerMetadata,
                null),
            [
                typeof(object).Assembly.Location,
                typeof(ReactiveObject).Assembly.Location,
            ],
            [
                new PostGeneratorMemberRequirement(
                    "amount",
                    nameof(GeneratedReactiveViewModel.Amount),
                    PostGeneratorMemberKind.Property,
                    "System.Int32",
                    null,
                    true,
                    false),
            ]);
        PostGeneratorSemanticResult result = PostGeneratorSemanticCompiler.Compile(request);
        Equal(0, result.Diagnostics.Count);
        Equal(1, result.Artifacts.Count);
        True(result.Artifacts[0].Source.Contains(
            "viewModel.Amount",
            StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static async Task PropertyCommandAndResultAsync()
    {
        var model = new GeneratedReactiveViewModel();
        await using ReactiveUiMvvmBindingAdapter<GeneratedReactiveViewModel> adapter =
            CreateAdapter(model, ImmediateScheduler.Instance, []);
        MvvmBindingResult property = await adapter.DispatchAsync(
            Mutation(MvvmMutationKind.SetProperty, 1, "7"),
            CancellationToken.None);
        True(property.Succeeded);
        Equal(7, model.Amount);

        MvvmBindingResult command = await adapter.DispatchAsync(
            Mutation(MvvmMutationKind.ExecuteCommand, 2, "7"),
            CancellationToken.None);
        True(command.Succeeded);
        Equal(1, model.Submissions);
        Equal(14, command.Payload!.Value.GetInt32());
        True(command.Patches.Any(static patch =>
            patch is MvvmCommandPatch { MemberId: 2, IsExecuting: false }));
    }

    private static async Task ActivationSchedulerAndDisposalAsync()
    {
        var model = new GeneratedReactiveViewModel();
        var scheduler = new CountingScheduler();
        await using ReactiveUiMvvmBindingAdapter<GeneratedReactiveViewModel> adapter =
            CreateAdapter(model, scheduler, []);
        Equal(1, model.ActivationCount);
        True(adapter.OwnedLeaseCount >= 4);
        True(scheduler.ScheduleCount >= 3);
        await adapter.DisposeAsync();
        await adapter.DisposeAsync();
        Equal(1, model.DeactivationCount);
        await ThrowsAsync<ObjectDisposedException>(
            async () => await adapter.SnapshotAsync(CancellationToken.None));
    }

    private static async Task CommandFaultRoutingAsync()
    {
        var faults = new List<Exception>();
        var model = new GeneratedReactiveViewModel();
        await using ReactiveUiMvvmBindingAdapter<GeneratedReactiveViewModel> adapter =
            CreateAdapter(model, ImmediateScheduler.Instance, faults);
        MvvmBindingResult failure = await adapter.DispatchAsync(
            Mutation(MvvmMutationKind.ExecuteCommand, 3, "1"),
            CancellationToken.None);
        False(failure.Succeeded);
        True(failure.Committed);
        Equal(MvvmFaultCodes.RequestInvalid, failure.Fault!.Code);
        True(faults.Count >= 1);
        True(failure.Fault.Message.Contains("failed", StringComparison.Ordinal));
        False(failure.Fault.Message.Contains("secret", StringComparison.Ordinal));
    }

    private static async Task VerticalAsync()
    {
        var model = new GeneratedReactiveViewModel();
        await using ReactiveUiMvvmBindingAdapter<GeneratedReactiveViewModel> adapter =
            CreateAdapter(model, ImmediateScheduler.Instance, []);
        await adapter.DispatchAsync(
            Mutation(MvvmMutationKind.SetProperty, 1, "7"),
            CancellationToken.None);
        MvvmBindingResult result = await adapter.DispatchAsync(
            Mutation(MvvmMutationKind.ExecuteCommand, 2, "7"),
            CancellationToken.None);
        Equal(7, model.Amount);
        Equal(1, model.Submissions);
        Equal(14, result.Payload!.Value.GetInt32());
        Console.WriteLine(
            "G6-VERTICAL: reactiveui/amount-submit-v1 amount=7 submissions=1 commits=2");
    }

    private static ReactiveUiMvvmBindingAdapter<GeneratedReactiveViewModel> CreateAdapter(
        GeneratedReactiveViewModel model,
        IScheduler scheduler,
        List<Exception> faults) =>
        new ReactiveUiMvvmAdapterBuilder<GeneratedReactiveViewModel>(model)
            .ObserveOn(scheduler)
            .ActivateWith(static viewModel => viewModel.Activator.Activate())
            .RouteFaultsTo(faults.Add)
            .BindProperty(
                1,
                nameof(GeneratedReactiveViewModel.Amount),
                static viewModel => viewModel.Amount,
                static (viewModel, value) => viewModel.Amount = value,
                ReactiveJsonContext.Default.Int32)
            .BindCommand(
                2,
                nameof(GeneratedReactiveViewModel.SubmitCommand),
                static viewModel => viewModel.SubmitCommand,
                ReactiveJsonContext.Default.Int32,
                ReactiveJsonContext.Default.Int32)
            .BindCommand(
                3,
                nameof(GeneratedReactiveViewModel.FailCommand),
                static viewModel => viewModel.FailCommand,
                ReactiveJsonContext.Default.Int32,
                ReactiveJsonContext.Default.Int32)
            .Build();

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

    private static async Task RunAsync(string fixture, Func<Task> test)
    {
        await test().ConfigureAwait(false);
        _passed++;
        Console.WriteLine($"PASS: {fixture}");
    }

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
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

    private sealed class CountingScheduler : IScheduler
    {
        public int ScheduleCount { get; private set; }
        public DateTimeOffset Now => ImmediateScheduler.Instance.Now;

        public IDisposable Schedule<TState>(
            TState state,
            Func<IScheduler, TState, IDisposable> action)
        {
            ScheduleCount++;
            return ImmediateScheduler.Instance.Schedule(state, action);
        }

        public IDisposable Schedule<TState>(
            TState state,
            TimeSpan dueTime,
            Func<IScheduler, TState, IDisposable> action)
        {
            ScheduleCount++;
            return ImmediateScheduler.Instance.Schedule(state, dueTime, action);
        }

        public IDisposable Schedule<TState>(
            TState state,
            DateTimeOffset dueTime,
            Func<IScheduler, TState, IDisposable> action)
        {
            ScheduleCount++;
            return ImmediateScheduler.Instance.Schedule(state, dueTime, action);
        }
    }

    [JsonSerializable(typeof(int))]
    private sealed partial class ReactiveJsonContext : JsonSerializerContext
    {
    }
}

public sealed partial class GeneratedReactiveViewModel :
    ReactiveObject,
    IActivatableViewModel
{
    [Reactive]
    private int _amount;

    public int Submissions { get; private set; }

    public int ActivationCount { get; private set; }

    public int DeactivationCount { get; private set; }

    public ViewModelActivator Activator { get; } = new();

    public GeneratedReactiveViewModel()
    {
        this.WhenActivated(disposables =>
        {
            ActivationCount++;
            disposables.Add(Disposable.Create(() => DeactivationCount++));
        });
    }

    [ReactiveCommand]
    private int Submit(int amount)
    {
        Submissions++;
        return amount * 2;
    }

    [ReactiveCommand]
    private int Fail(int value) =>
        throw new InvalidOperationException($"secret reactive failure {value}");
}
