using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace NotesReactiveViews;

public interface IMainPage : IRoutableViewModel;
public interface IDocumentPane : IRoutableViewModel;

/// <summary>A pane of the document page.</summary>
public enum DocumentPane
{
    /// <summary>The note editor.</summary>
    Editor,

    /// <summary>The rendered note.</summary>
    Preview,
}
public interface IPinnedItem;

public sealed class ShellViewModel : ReactiveObject, IScreen, IDisposable
{
    private readonly IRunicModelContextLease _modelContextLease;
    private readonly HomeViewModel _home;
    private readonly DocumentViewModel _document;
    private readonly ReactiveRoutedRegion<IMainPage> _main;
    private readonly PinnedNoteViewModel _pinnedNote = new();
    private readonly PinnedTaskViewModel _pinnedTask = new();
    private IReadOnlyList<IPinnedItem> _pinned;

    public ShellViewModel(IRunicModelContext modelContext, ISequencer scheduler, ILoggerFactory loggerFactory)
    {
        _pinned = [_pinnedNote, _pinnedTask];
        _home = new HomeViewModel(this);
        _document = new DocumentViewModel(this, modelContext, scheduler, loggerFactory);
        _modelContextLease = RunicModelContextRegistry.Shared.Bind(modelContext,
            this, _home, _document, _document.Editor, _document.Preview, _pinnedNote, _pinnedTask);
        Router = new RoutingState(scheduler);
        _main = new ReactiveRoutedRegion<IMainPage>(Router, loggerFactory);
        _main.PropertyChanged += OnMainChanged;
        OpenHomeCommand = ReactiveCommand.Create(OpenHome, scheduler);
        OpenDocumentCommand = ReactiveCommand.Create(OpenDocument, scheduler);
        SwapPinnedCommand = ReactiveCommand.Create(SwapPinned, scheduler);
        RemovePinnedCommand = ReactiveCommand.Create(RemovePinned, scheduler);
        RestorePinnedCommand = ReactiveCommand.Create(RestorePinned, scheduler);
        Router.Navigate.Execute(_home).Subscribe(_ => { });
    }

    [RunicIgnore] public RoutingState Router { get; }
    public IMainPage Main => _main.Current ?? _home;
    public IReadOnlyList<IPinnedItem> Pinned => _pinned;
    internal EditorViewModel Editor => _document.Editor;
    public ReactiveCommand<RxVoid, RxVoid> OpenHomeCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenDocumentCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SwapPinnedCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> RemovePinnedCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> RestorePinnedCommand { get; }

    private void OpenHome() => Router.Navigate.Execute(_home).Subscribe(_ => { });
    private void OpenDocument() => Router.Navigate.Execute(_document).Subscribe(_ => { });
    private void SwapPinned()
    {
        _pinned = [_pinnedTask, _pinnedNote];
        this.RaisePropertyChanged(nameof(Pinned));
    }
    private void RemovePinned()
    {
        _pinned = [_pinnedTask];
        this.RaisePropertyChanged(nameof(Pinned));
    }
    private void RestorePinned()
    {
        _pinned = [_pinnedNote, _pinnedTask];
        this.RaisePropertyChanged(nameof(Pinned));
    }
    private void OnMainChanged(object? sender, PropertyChangedEventArgs args) =>
        this.RaisePropertyChanged(nameof(Main));
    public void Dispose()
    {
        _main.PropertyChanged -= OnMainChanged;
        _main.Dispose();
        _document.Dispose();
        _modelContextLease.Dispose();
    }
}

public class PinnedNoteViewModel : ReactiveObject, IPinnedItem
{
    public string Label => "Pinned note";
}

public sealed class PinnedTaskViewModel : PinnedNoteViewModel
{
    public string Priority => "High";
}

public sealed partial class HomeViewModel(IScreen host) : ReactiveObject, IMainPage
{
    public string UrlPathSegment => "home";
    [RunicIgnore] public IScreen HostScreen => host;
    public string Greeting => "Reactive Notes";
}

public sealed class DocumentViewModel : ReactiveObject, IMainPage, IScreen, IDisposable
{
    private readonly EditorViewModel _editor;
    private readonly PreviewViewModel _preview;
    private readonly ReactiveRoutedRegion<IDocumentPane> _pane;

    public DocumentViewModel(ShellViewModel host, IRunicModelContext modelContext, ISequencer scheduler,
        ILoggerFactory loggerFactory)
    {
        HostScreen = host;
        _editor = new EditorViewModel(this, modelContext, scheduler, loggerFactory.CreateLogger<EditorViewModel>());
        _preview = new PreviewViewModel(this, _editor);
        Router = new RoutingState(scheduler);
        _pane = new ReactiveRoutedRegion<IDocumentPane>(Router, loggerFactory);
        _pane.PropertyChanged += OnPaneChanged;
        ShowEditorCommand = ReactiveCommand.Create(ShowEditor, scheduler);
        ShowPreviewCommand = ReactiveCommand.Create(ShowPreview, scheduler);
        Router.Navigate.Execute(_editor).Subscribe(_ => { });
    }

    public string UrlPathSegment => "document";
    [RunicIgnore] public IScreen HostScreen { get; }
    [RunicIgnore] public RoutingState Router { get; }
    public IDocumentPane CurrentPane => _pane.Current ?? _editor;
    internal EditorViewModel Editor => _editor;
    internal PreviewViewModel Preview => _preview;
    [RunicViewContract("compact")]
    public EditorViewModel CompactNote => _editor;
    /// <summary>The pane the nested router currently shows.</summary>
    public DocumentPane ActivePane => ReferenceEquals(CurrentPane, _editor) ? DocumentPane.Editor : DocumentPane.Preview;
    public ReactiveCommand<RxVoid, RxVoid> ShowEditorCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ShowPreviewCommand { get; }

    private void ShowEditor() => Router.Navigate.Execute(_editor).Subscribe(_ => { });
    private void ShowPreview() => Router.Navigate.Execute(_preview).Subscribe(_ => { });
    private void OnPaneChanged(object? sender, PropertyChangedEventArgs args)
    {
        this.RaisePropertyChanged(nameof(CurrentPane));
        this.RaisePropertyChanged(nameof(ActivePane));
    }
    public void Dispose()
    {
        _pane.PropertyChanged -= OnPaneChanged;
        _pane.Dispose();
        _preview.Dispose();
        _editor.Dispose();
    }
}

public sealed record DiscardNoteRequest(string Title, int BodyLength);

/// <summary>Why a note could not be saved.</summary>
[RunicUnion(typeof(TitleRequired), typeof(TitleTooLong))]
public abstract record SaveFailure;
/// <summary>The note has no title.</summary>
[RunicUnionCase("titleRequired")]
public sealed record TitleRequired : SaveFailure;
/// <summary>The title is longer than the notes store accepts.</summary>
[RunicUnionCase("titleTooLong")]
public sealed record TitleTooLong(int MaximumLength) : SaveFailure;

public sealed class EditorViewModel : ReactiveObject, IDocumentPane, IActivatableViewModel, IDisposable
{
    private readonly IRunicModelContext _modelContext;
    private const int MaximumTitleLength = 120;
    private readonly IDisposable _fallbackDiscardHandler;
    private readonly IDisposable _saveExceptions;
    private readonly IDisposable _discardExceptions;
    private string _title = "Untitled";
    private string _body = "";
    private string _savedMessage = "";
    private int _activationCount;
    private int _deactivationCount;

    public EditorViewModel(DocumentViewModel host, IRunicModelContext modelContext, ISequencer scheduler, ILogger logger)
    {
        HostScreen = host;
        _modelContext = modelContext;
        _fallbackDiscardHandler = ConfirmDiscard.RegisterHandler(context =>
        {
            // A native or headless invocation has no mounted browser endpoint.
            // Keep ReactiveUI's normal handler precedence and decline the discard.
            context.SetOutput(false);
            return Task.CompletedTask;
        });
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, scheduler);
        DiscardCommand = ReactiveCommand.CreateFromTask(DiscardAsync, scheduler);
        // A ReactiveCommand also reports its exceptions on ThrownExceptions. The
        // Bridge already delivered declared failures; log anything else.
        _saveExceptions = SaveCommand.ObserveBridgeExceptions(logger);
        _discardExceptions = DiscardCommand.ObserveBridgeExceptions(logger);
        this.WhenActivated((Action<Action<IDisposable>>)(dispose =>
        {
            ActivationCount++;
            dispose(new ActivationLease(() => DeactivationCount++));
        }));
    }

    public string UrlPathSegment => "editor";
    [RunicIgnore] public IScreen HostScreen { get; }
    [RunicIgnore] public ViewModelActivator Activator { get; } = new();
    public string Title { get => _title; set => this.RaiseAndSetIfChanged(ref _title, value); }
    public string Body { get => _body; set => this.RaiseAndSetIfChanged(ref _body, value); }
    public string SavedMessage { get => _savedMessage; private set => this.RaiseAndSetIfChanged(ref _savedMessage, value); }
    public int ActivationCount { get => _activationCount; private set => this.RaiseAndSetIfChanged(ref _activationCount, value); }
    public int DeactivationCount { get => _deactivationCount; private set => this.RaiseAndSetIfChanged(ref _deactivationCount, value); }
    public Interaction<DiscardNoteRequest, bool> ConfirmDiscard { get; } = new();
    /// <summary>Saves the note. A missing or overlong title is a declared failure.</summary>
    [RunicFailure(typeof(SaveFailure))]
    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> DiscardCommand { get; }

    private async Task SaveAsync(CancellationToken token)
    {
        var input = await _modelContext.InvokeAsync(() => new { Title, Body }, token);
        if (string.IsNullOrWhiteSpace(input.Title)) throw new RunicFailureException(new TitleRequired());
        if (input.Title.Length > MaximumTitleLength) throw new RunicFailureException(new TitleTooLong(MaximumTitleLength));
        await Task.Delay(120, token);
        await _modelContext.InvokeAsync(() => SavedMessage = $"Saved {input.Title}", token);
    }

    private async Task DiscardAsync(CancellationToken token)
    {
        var request = await _modelContext.InvokeAsync(() => new DiscardNoteRequest(Title, Body.Length), token);
        if (request.BodyLength == 0)
        {
            await _modelContext.InvokeAsync(() => SavedMessage = "Nothing to discard.", token);
            return;
        }

        bool approved = await ConfirmDiscard.Handle(request);
        token.ThrowIfCancellationRequested();
        await _modelContext.InvokeAsync(() =>
        {
            if (approved)
            {
                Body = "";
                SavedMessage = $"Discarded {request.Title}";
            }
            else SavedMessage = "Kept current changes.";
        }, token);
    }

    public void Dispose()
    {
        _fallbackDiscardHandler.Dispose();
        _saveExceptions.Dispose();
        _discardExceptions.Dispose();
        SaveCommand.Dispose();
        DiscardCommand.Dispose();
    }

    private sealed class ActivationLease(Action dispose) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (!_disposed) { _disposed = true; dispose(); } }
    }
}

public sealed class PreviewViewModel : ReactiveObject, IDocumentPane, IDisposable
{
    private readonly EditorViewModel _editor;
    public PreviewViewModel(DocumentViewModel host, EditorViewModel editor)
    {
        HostScreen = host;
        _editor = editor;
        _editor.PropertyChanged += OnEditorChanged;
    }
    public string UrlPathSegment => "preview";
    [RunicIgnore] public IScreen HostScreen { get; }
    public string Heading => _editor.Title;
    public string Body => _editor.Body;
    private void OnEditorChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EditorViewModel.Title)) this.RaisePropertyChanged(nameof(Heading));
        if (args.PropertyName == nameof(EditorViewModel.Body)) this.RaisePropertyChanged(nameof(Body));
    }
    public void Dispose() => _editor.PropertyChanged -= OnEditorChanged;
}
