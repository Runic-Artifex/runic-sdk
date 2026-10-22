using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

// #284 (#61): navigation pages whose operations declare what happens when the page's entry retires.
public sealed partial class RetireShellWindow : RunicWindow<RetireShellViewModel>;

public sealed partial class RetireCoverView : RunicView<RetireCoverViewModel>;

public sealed partial class RetireReactivePageView : RunicView<RetireReactivePageViewModel>;

public sealed partial class RetireDetailView : RunicView<RetireDetailViewModel>;

public sealed partial class RetireToolkitPageView : RunicView<RetireToolkitPageViewModel>;

public sealed partial class RetireLatePageView : RunicView<RetireLatePageViewModel>;

/// <summary>Content of the retirement shell's region.</summary>
public interface IRetirePage : INotifyPropertyChanged
{
    /// <summary>The page title.</summary>
    string Title { get; }
}

public sealed class RetireShellViewModel : INotifyPropertyChanged
{
    public RetireShellViewModel(RunicNavigator navigator) =>
        Main = navigator.CreateRegion<IRetirePage>(this, NavigationTarget.Borrow<IRetirePage>(new RetireCoverViewModel("home")));

    /// <summary>The page; the navigator changes it.</summary>
    public NavigationRegion<IRetirePage> Main { get; }

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed class RetireCoverViewModel(string title) : IRetirePage
{
    public string Title { get; } = title;

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

// One operation's work: it runs until the test releases it or its token is cancelled.
public sealed class RetireWork(string name)
{
    private int _ended;

    public string Name { get; } = name;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Cancelled { get; private set; }
    public bool Ended => Volatile.Read(ref _ended) == 1;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        try { await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            Cancelled = true;
            throw;
        }
        finally { Volatile.Write(ref _ended, 1); }
    }
}

// Records which of its works were still running when the navigator disposed it.
public abstract class RetirePageRecord
{
    private int _disposed;

    internal abstract IEnumerable<RetireWork> Works { get; }
    internal int Disposed => Volatile.Read(ref _disposed);
    internal IReadOnlyList<string> RunningAtDispose { get; private set; } = [];

    protected void RecordDisposal()
    {
        RunningAtDispose = [.. Works.Where(work => work.Started.Task.IsCompleted && !work.Ended).Select(work => work.Name)];
        Interlocked.Increment(ref _disposed);
    }
}

// ReactiveUI (ReactiveUI.Primitives flavor): the policy on each command property, and a presented child whose
// operation drains with the page.
public sealed class RetireReactivePageViewModel : ReactiveObject, IRetirePage, IDisposable
{
    private readonly RetirePageState _state = new();

    public RetireReactivePageViewModel(NavigationEntryContext entry, IRunicModelContext context)
    {
        Entry = entry;
        var scheduler = ReactiveSchedulers.For(context);
        KeepCommand = ReactiveCommand.CreateFromTask(token => _state.Keep.RunAsync(token), outputScheduler: scheduler);
        CancelCommand = ReactiveCommand.CreateFromTask(token => _state.Cancel.RunAsync(token), outputScheduler: scheduler);
        DrainCommand = ReactiveCommand.CreateFromTask(token => _state.Drain.RunAsync(token), outputScheduler: scheduler);
        Detail = new RetireDetailViewModel(scheduler);
    }

    public string Title => "reactive";

    internal NavigationEntryContext Entry { get; }

    internal RetirePageState State => _state;

    public RetireDetailViewModel Detail { get; }

    public ReactiveCommand<RxVoid, RxVoid> KeepCommand { get; }

    [RunicOperationRetirement(RunicOperationRetirement.Cancel)]
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    [RunicOperationRetirement(RunicOperationRetirement.Drain)]
    public ReactiveCommand<RxVoid, RxVoid> DrainCommand { get; }

    public void Dispose() => _state.Dispose(Detail.Sync);

    internal sealed class RetirePageState : RetirePageRecord
    {
        private RetireWork? _detail;

        public RetireWork Keep { get; } = new("Keep");
        public RetireWork Cancel { get; } = new("Cancel");
        public RetireWork Drain { get; } = new("Drain");

        internal override IEnumerable<RetireWork> Works => _detail is null ? [Keep, Cancel, Drain] : [Keep, Cancel, Drain, _detail];

        public void Dispose(RetireWork detail)
        {
            _detail = detail;
            RecordDisposal();
        }
    }
}

// I-188: Cancel operations whose work notices the cancellation only once the test releases it, so ReactiveUI reports
// their end on ThrownExceptions after the page's Bridge detached. The ViewModel does not subscribe to ThrownExceptions.
public sealed class RetireLatePageViewModel : ReactiveObject, IRetirePage, IDisposable
{
    private int _disposed;

    public RetireLatePageViewModel(IRunicModelContext context)
    {
        var scheduler = ReactiveSchedulers.For(context);
        UploadCommand = ReactiveCommand.CreateFromTask(token => RunLateAsync(failure: null, token), outputScheduler: scheduler);
        ExportCommand = ReactiveCommand.CreateFromTask(token => RunLateAsync(Failure, token), outputScheduler: scheduler);
    }

    public string Title => "late";

    internal TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ExportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal InvalidOperationException Failure { get; } = new("disk full");
    internal int Disposed => Volatile.Read(ref _disposed);

    // Ends cancelled once released.
    [RunicOperationRetirement(RunicOperationRetirement.Cancel)]
    public ReactiveCommand<RxVoid, RxVoid> UploadCommand { get; }

    // Fails with Failure once released, after its call was cancelled: no caller receives it.
    [RunicOperationRetirement(RunicOperationRetirement.Cancel)]
    public ReactiveCommand<RxVoid, RxVoid> ExportCommand { get; }

    public void Dispose() => Interlocked.Increment(ref _disposed);

    private async Task RunLateAsync(Exception? failure, CancellationToken cancellationToken)
    {
        (failure is null ? UploadStarted : ExportStarted).TrySetResult();
        await Release.Task.ConfigureAwait(false);
        if (failure is not null) throw failure;
        cancellationToken.ThrowIfCancellationRequested();
    }
}

// Presented by RetireReactivePageViewModel.Detail; its operation starts on its own route.
public sealed class RetireDetailViewModel : ReactiveObject
{
    public RetireDetailViewModel(ReactiveUI.Primitives.Concurrency.ISequencer scheduler) =>
        SyncCommand = ReactiveCommand.CreateFromTask(token => Sync.RunAsync(token), outputScheduler: scheduler);

    internal RetireWork Sync { get; } = new("Detail");

    [RunicOperationRetirement(RunicOperationRetirement.Drain)]
    public ReactiveCommand<RxVoid, RxVoid> SyncCommand { get; }
}

// CommunityToolkit: the class declares Cancel; [property:] forwarding overrides it per command.
[RunicOperationRetirement(RunicOperationRetirement.Cancel)]
public sealed partial class RetireToolkitPageViewModel : ObservableObject, IRetirePage, IDisposable
{
    private readonly ToolkitState _state = new();

    public string Title => "toolkit";

    internal ToolkitState State => _state;

    [RelayCommand]
    private Task UploadAsync(CancellationToken cancellationToken) => _state.Upload.RunAsync(cancellationToken);

    [RelayCommand]
    [property: RunicOperationRetirement(RunicOperationRetirement.Drain)]
    private Task ExportAsync(CancellationToken cancellationToken) => _state.Export.RunAsync(cancellationToken);

    [RelayCommand]
    [property: RunicOperationRetirement(RunicOperationRetirement.Keep)]
    private Task RefreshAsync(CancellationToken cancellationToken) => _state.Refresh.RunAsync(cancellationToken);

    public void Dispose() => _state.Dispose();

    internal sealed class ToolkitState : RetirePageRecord
    {
        public RetireWork Upload { get; } = new("Upload");
        public RetireWork Export { get; } = new("Export");
        public RetireWork Refresh { get; } = new("Refresh");

        internal override IEnumerable<RetireWork> Works => [Upload, Export, Refresh];

        public void Dispose() => RecordDisposal();
    }
}

internal sealed class RetireViewLocator : IRunicViewLocator
{
    public TView Locate<TView, TViewModel>() where TView : class, IRunicView where TViewModel : class =>
        typeof(TView) == typeof(RetireCoverView) ? (TView)(object)new RetireCoverView()
        : typeof(TView) == typeof(RetireReactivePageView) ? (TView)(object)new RetireReactivePageView()
        : typeof(TView) == typeof(RetireDetailView) ? (TView)(object)new RetireDetailView()
        : typeof(TView) == typeof(RetireToolkitPageView) ? (TView)(object)new RetireToolkitPageView()
        : typeof(TView) == typeof(RetireLatePageView) ? (TView)(object)new RetireLatePageView()
        : throw new InvalidOperationException($"Unexpected View {typeof(TView).Name}.");
}
