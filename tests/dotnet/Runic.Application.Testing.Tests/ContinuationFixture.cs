using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Navigation;
using Runic.Application.Views.ReactiveUI;

namespace Runic.Application.Testing.Tests;

// One write of a probed command body: where it ran and whether it ran in a model turn.
public sealed record ContinuationStep(string Label, bool InTurn);

// Shared by both MVVM flavours (D018.5). The command body awaits a yield, a timer,
// a nested async method and a gate the test releases, and writes ViewModel state
// after each await, as the template and tutorial do.
public sealed class ContinuationProbe
{
    private readonly List<ContinuationStep> _steps = [];
    private readonly Dictionary<string, TaskCompletionSource> _waiting = [];
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IRunicModelContext? Context { get; set; }

    public IReadOnlyList<ContinuationStep> Steps { get { lock (_steps) return [.. _steps]; } }

    public void Record(string label)
    {
        var inTurn = Context?.IsExecuting == true;
        lock (_steps) _steps.Add(new(label, inTurn));
    }

    public Task Release => _release.Task;

    public void ReleaseAll() => _release.TrySetResult();

    public void Reset()
    {
        lock (_steps) _steps.Clear();
        _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task WaitingFor(string label)
    {
        lock (_waiting)
        {
            if (!_waiting.TryGetValue(label, out var waiting))
                _waiting[label] = waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return waiting.Task;
        }
    }

    public void SignalWaiting(string label)
    {
        lock (_waiting)
        {
            if (!_waiting.TryGetValue(label, out var waiting))
                _waiting[label] = waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting.TrySetResult();
        }
    }

    // The body both flavours run. Every Write is a ViewModel property set.
    public async Task RunAsync(string label, Action<string> write)
    {
        write($"{label}:start");
        await Task.Yield();
        write($"{label}:yield");
        await Task.Delay(1);
        write($"{label}:delay");
        await NestedAsync(label, write);
        write($"{label}:nested");
        var release = Release;
        SignalWaiting(label);
        await release;
        write($"{label}:released");
    }

    private static async Task NestedAsync(string label, Action<string> write)
    {
        await Task.Delay(1);
        write($"{label}:inner");
    }
}

public sealed partial class ToolkitContinuationViewModel : ObservableObject
{
    public ToolkitContinuationViewModel(ContinuationProbe probe)
    {
        Probe = probe;
        PropertyChanged += OnStatusChanged;
    }

    internal ContinuationProbe Probe { get; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [RelayCommand]
    private Task RunAsync(string label) => Probe.RunAsync(label, value => Status = value);

    // A second command: like ReactiveUI, a Toolkit command does not overlap its own executions.
    [RelayCommand]
    private Task RunNextAsync(string label) => Probe.RunAsync(label, value => Status = value);

    // Records where PropertyChanged is raised, which is where the bridge observes the write.
    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Status)) Probe.Record(Status);
    }
}

public sealed partial class ToolkitContinuationWindow
    : RunicWindow<ToolkitContinuationViewModel>;

public sealed class ReactiveContinuationViewModel : ReactiveObject
{
    private string _status = "";

    public ReactiveContinuationViewModel(IRunicModelContext modelContext, ContinuationProbe probe)
    {
        ArgumentNullException.ThrowIfNull(modelContext);
        Probe = probe;
        var scheduler = ReactiveSchedulers.For(modelContext);
        RunCommand = ReactiveCommand.CreateFromTask<string>(label => Probe.RunAsync(label, value => Status = value),
            outputScheduler: scheduler);
        RunNextCommand = ReactiveCommand.CreateFromTask<string>(label => Probe.RunAsync(label, value => Status = value),
            outputScheduler: scheduler);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Status)) Probe.Record(Status);
        };
    }

    internal ContinuationProbe Probe { get; }

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public IReactiveCommand<string, RxVoid> RunCommand { get; }

    public IReactiveCommand<string, RxVoid> RunNextCommand { get; }
}

public sealed partial class ReactiveContinuationWindow
    : RunicWindow<ReactiveContinuationViewModel>;
