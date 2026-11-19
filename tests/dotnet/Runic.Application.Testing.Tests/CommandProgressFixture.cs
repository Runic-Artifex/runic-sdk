using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

// #1180: an asynchronous command that takes IProgress<double>. The Bridge binds it to the running operation
// (RunicOperationProgress.Current), so the page sends no input for it. The work reports once on the command's flow and
// once from a worker thread, then waits for the test.
public sealed class CommandProgressProbe
{
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IProgress<double>? Received { get; private set; }

    public void ReleaseAll() => _release.TrySetResult();

    public void Reset() => _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RunAsync(IProgress<double> progress, CancellationToken token)
    {
        Received = progress;
        progress.Report(0.25);
        await Task.Run(() => progress.Report(0.5), token).ConfigureAwait(false);
        await _release.Task.WaitAsync(token).ConfigureAwait(false);
    }
}

public sealed partial class ToolkitProgressViewModel(CommandProgressProbe probe) : ObservableObject
{
    internal CommandProgressProbe Probe { get; } = probe;

    [RelayCommand]
    private Task ImportAsync(IProgress<double> progress, CancellationToken token) => Probe.RunAsync(progress, token);
}

public sealed partial class ToolkitProgressWindow
    : RunicWindow<ToolkitProgressViewModel>;

public sealed class ReactiveProgressViewModel : ReactiveObject
{
    public ReactiveProgressViewModel(IRunicModelContext modelContext, CommandProgressProbe probe)
    {
        ArgumentNullException.ThrowIfNull(modelContext);
        Probe = probe;
        ImportCommand = ReactiveCommand.CreateFromTask<IProgress<double>>((progress, token) => Probe.RunAsync(progress, token),
            outputScheduler: ReactiveSchedulers.For(modelContext));
    }

    internal CommandProgressProbe Probe { get; }

    public IReactiveCommand<IProgress<double>, RxVoid> ImportCommand { get; }
}

public sealed partial class ReactiveProgressWindow
    : RunicWindow<ReactiveProgressViewModel>;
