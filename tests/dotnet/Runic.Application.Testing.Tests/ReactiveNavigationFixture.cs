using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives;
using ReactiveUI.SourceGenerators;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;
using Runic.Navigation.ReactiveUI;

namespace Runic.Application.Testing.Tests;

// A framework-native command and observable property over the shared navigation engine.
// The same ViewModel works with a native ICommand consumer and the generated web bridge.
public sealed partial class ReactiveNavShellViewModel : ReactiveObject, IDisposable
{
    private string _outcome = "none";

    public ReactiveNavShellViewModel(RunicNavigator navigator, IRunicModelContext context, NavHomeViewModel home)
    {
        Main = navigator.CreateRegion<INavPageViewModel>(this, NavigationTarget.Borrow<INavPageViewModel>(home));
        var scheduler = new RunicReactiveSchedulerProvider().For(context);
        _busyHelper = Main.WhenIsTransitioningChanged().ToProperty(this, model => model.Busy);
        OpenCommand = ReactiveCommand.CreateFromTask(async token =>
        {
            var result = await Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(new NavEditorViewModel("editor")),
                cancellationToken: token);
            // Publish the settled core outcome even when the command's caller cancelled.
            await context.InvokeAsync(() => Outcome = result switch
            {
                NavigationResult<INavPageViewModel>.Committed => "committed",
                NavigationResult<INavPageViewModel>.Rejected rejected => rejected.Reason.ToString().ToLowerInvariant(),
                NavigationResult<INavPageViewModel>.Superseded => "superseded",
                _ => "failed",
            }, CancellationToken.None);
        }, Main.WhenIsTransitioningChanged().Select(busy => !busy), scheduler);
        // No scheduler: AddRunicReactiveModelContext or InstallMainThreadScheduler routes it to the turn's context.
        BackCommand = Main.CreateBackCommand();
    }

    public NavigationRegion<INavPageViewModel> Main { get; }

    [ObservableAsProperty]
    public partial bool Busy { get; }

    public string Outcome
    {
        get => _outcome;
        private set => this.RaiseAndSetIfChanged(ref _outcome, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> OpenCommand { get; }

    // A NavigationOutcome crosses the Bridge without RUNICBRIDGE003 (W250-014).
    public ReactiveCommand<RxVoid, NavigationOutcome> BackCommand { get; }

    public void Dispose()
    {
        OpenCommand.Dispose();
        BackCommand.Dispose();
        _busyHelper?.Dispose();
    }
}

public sealed partial class ReactiveNavShellWindow
    : RunicWindow<ReactiveNavShellViewModel>;

/// <summary>The question a <see cref="ReactiveNavEditorViewModel"/> asks before it discards its draft.</summary>
public sealed record NavDiscardPrompt(string Title, string Draft);

// W250-034/035: content that takes its entry context in the constructor, ends its entry with result commands and asks
// before leaving through an interaction that the web View answers. It bridges without diagnostics.
public sealed class ReactiveNavEditorViewModel : ReactiveObject, INavPageViewModel, INavigationDepartureGuard, IDisposable
{
    private readonly LeaveConfirmation _leave;
    private string _draft = "";
    private string _saved = "";

    public ReactiveNavEditorViewModel(NavigationEntryContext entry)
    {
        _leave = ReactiveLeaveConfirmation.WithInteraction(ConfirmDiscard, () => new NavDiscardPrompt(Title, Draft),
            () => Draft != _saved, discard: () => Draft = _saved);
        SaveCommand = entry.CreateCompleteCommand(_ => Task.FromResult(_saved = Draft));
        CancelCommand = entry.CreateDismissCommand();
        CloseCommand = entry.CreateCloseCommand();
    }

    public string Title => "reactive editor";

    public string Draft
    {
        get => _draft;
        set => this.RaiseAndSetIfChanged(ref _draft, value);
    }

    public Interaction<NavDiscardPrompt, bool> ConfirmDiscard { get; } = new();

    public ReactiveCommand<RxVoid, NavigationOutcome> SaveCommand { get; }

    public ReactiveCommand<RxVoid, NavigationOutcome> CancelCommand { get; }

    public ReactiveCommand<RxVoid, NavigationOutcome> CloseCommand { get; }

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        _leave.CanDepartAsync(departure, cancellationToken);

    public void Dispose()
    {
        SaveCommand.Dispose();
        CancelCommand.Dispose();
        CloseCommand.Dispose();
    }
}

public sealed partial class ReactiveNavEditorView : RunicView<ReactiveNavEditorViewModel>;
