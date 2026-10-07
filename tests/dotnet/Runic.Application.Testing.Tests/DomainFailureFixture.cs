using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using System.Windows.Input;

namespace Runic.Application.Testing.Tests;

// W130-029 failure shapes: a [RunicUnion], an enum and a DTO.
[RunicUnion(typeof(NoteTitleRequired), typeof(NoteTitleTaken))]
public abstract record NoteSaveFailure;
[RunicUnionCase("titleRequired")]
public sealed record NoteTitleRequired : NoteSaveFailure;
[RunicUnionCase("titleTaken")]
public sealed record NoteTitleTaken(string ExistingTitle) : NoteSaveFailure;
public enum NotePublishBlock { Locked, Archived }
public sealed record NoteQuotaExceeded(int Limit, int Requested);

/// <summary>
/// Declared failures on every CommunityToolkit and plain command shape: a sync
/// RelayCommand and an AsyncRelayCommand declared on their [RelayCommand]
/// methods, and a plain ICommand declared on its property.
/// </summary>
public sealed partial class FailureToolkitViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "";

    public FailureToolkitViewModel() => ReserveCommand = new ReserveNotes(this);

    /// <summary>Saves the note; a missing or existing title is a declared failure.</summary>
    [RelayCommand, RunicFailure(typeof(NoteSaveFailure))]
    private void Save()
    {
        if (Title.Length == 0) throw new RunicFailureException(new NoteTitleRequired());
        if (Title == "taken") throw new RunicFailureException(new NoteTitleTaken("Todo"));
        Title = "saved";
    }

    [RelayCommand, RunicFailure(typeof(NotePublishBlock))]
    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (Title == "archived") throw new RunicFailureException(NotePublishBlock.Archived);
        Title = "published";
    }

    [RelayCommand]
    private Task DiscardAsync()
    {
        Title = "";
        return Task.CompletedTask;
    }

    [RunicFailure(typeof(NoteQuotaExceeded)), RunicCommandInput(typeof(int))]
    public ICommand ReserveCommand { get; }

    private sealed class ReserveNotes(FailureToolkitViewModel owner) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            var count = (int)parameter!;
            if (count > 3) throw new RunicFailureException(new NoteQuotaExceeded(3, count));
            owner.Title = $"reserved {count}";
        }
    }
}

/// <summary>A ReactiveUI command that declares its failure on the property.</summary>
public sealed class FailureReactiveViewModel : ReactiveObject, IDisposable
{
    private readonly IDisposable _exceptions;
    private readonly IDisposable _importExceptions;
    private string _title = "";

    public FailureReactiveViewModel(IRunicModelContext modelContext)
    {
        ArgumentNullException.ThrowIfNull(modelContext);
        var scheduler = new RunicReactiveSchedulerProvider().For(modelContext);
        SaveCommand = ReactiveCommand.CreateFromTask<RxVoid, int>(async _ =>
        {
            await Task.Yield();
            if (Title.Length == 0) throw new RunicFailureException(new NoteTitleRequired());
            return Title.Length;
        }, scheduler);
        // A stream that publishes two values, then fails as declared.
        ImportCommand = ReactiveCommand.CreateFromObservable<RxVoid, int>(_ => new FailingSequence(), scheduler);
        // M1: bridged ReactiveUI commands need a ThrownExceptions subscriber.
        _exceptions = SaveCommand.ThrownExceptions.Subscribe(new IgnoredExceptions());
        _importExceptions = ImportCommand.ThrownExceptions.Subscribe(new IgnoredExceptions());
    }

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    [RunicFailure(typeof(NoteSaveFailure))]
    public ReactiveCommand<RxVoid, int> SaveCommand { get; }

    [RunicFailure(typeof(NoteSaveFailure)), RunicCommandResult(BridgeCommandResultCardinality.Stream)]
    public ReactiveCommand<RxVoid, int> ImportCommand { get; }

    public void Dispose()
    {
        _exceptions.Dispose();
        _importExceptions.Dispose();
    }

    private sealed class FailingSequence : IObservable<int>
    {
        public IDisposable Subscribe(IObserver<int> observer)
        {
            observer.OnNext(1);
            observer.OnNext(2);
            observer.OnError(new RunicFailureException(new NoteTitleTaken("Todo")));
            return new Unsubscribed();
        }

        private sealed class Unsubscribed : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class IgnoredExceptions : IObserver<Exception>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(Exception value) { }
    }
}

public sealed partial class FailureToolkitWindow(FailureToolkitViewModel model) : RunicWindow<FailureToolkitViewModel>(model);
public sealed partial class FailureReactiveWindow(FailureReactiveViewModel model) : RunicWindow<FailureReactiveViewModel>(model);
