using System.ComponentModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;

namespace NotesReactiveViews;

public interface IMainPage : IRoutableViewModel;
public interface IDocumentPane : IRoutableViewModel;

public sealed class ShellViewModel : ReactiveObject, IScreen, IDisposable
{
    private readonly HomeViewModel _home;
    private readonly DocumentViewModel _document;
    private readonly ReactiveRoutedRegion<IMainPage> _main;

    public ShellViewModel()
    {
        _home = new HomeViewModel(this);
        _document = new DocumentViewModel(this);
        _main = new ReactiveRoutedRegion<IMainPage>(Router);
        _main.PropertyChanged += OnMainChanged;
        OpenHomeCommand = ReactiveCommand.Create(OpenHome);
        OpenDocumentCommand = ReactiveCommand.Create(OpenDocument);
        Router.Navigate.Execute(_home).Subscribe(_ => { });
    }

    [RunicIgnore] public RoutingState Router { get; } = new();
    public IMainPage Main => _main.Current ?? _home;
    internal EditorViewModel Editor => _document.Editor;
    public ReactiveCommand<RxVoid, RxVoid> OpenHomeCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenDocumentCommand { get; }

    private void OpenHome() => Router.Navigate.Execute(_home).Subscribe(_ => { });
    private void OpenDocument() => Router.Navigate.Execute(_document).Subscribe(_ => { });
    private void OnMainChanged(object? sender, PropertyChangedEventArgs args) =>
        this.RaisePropertyChanged(nameof(Main));
    public void Dispose() { _main.PropertyChanged -= OnMainChanged; _main.Dispose(); }
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

    public DocumentViewModel(ShellViewModel host)
    {
        HostScreen = host;
        _editor = new EditorViewModel(this);
        _preview = new PreviewViewModel(this, _editor);
        _pane = new ReactiveRoutedRegion<IDocumentPane>(Router);
        _pane.PropertyChanged += OnPaneChanged;
        ShowEditorCommand = ReactiveCommand.Create(ShowEditor);
        ShowPreviewCommand = ReactiveCommand.Create(ShowPreview);
        Router.Navigate.Execute(_editor).Subscribe(_ => { });
    }

    public string UrlPathSegment => "document";
    [RunicIgnore] public IScreen HostScreen { get; }
    [RunicIgnore] public RoutingState Router { get; } = new();
    public IDocumentPane CurrentPane => _pane.Current ?? _editor;
    internal EditorViewModel Editor => _editor;
    [RunicViewContract("compact")]
    public EditorViewModel CompactNote => _editor;
    public string ActivePane => ReferenceEquals(CurrentPane, _editor) ? "Editor" : "Preview";
    public ReactiveCommand<RxVoid, RxVoid> ShowEditorCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ShowPreviewCommand { get; }

    private void ShowEditor() => Router.Navigate.Execute(_editor).Subscribe(_ => { });
    private void ShowPreview() => Router.Navigate.Execute(_preview).Subscribe(_ => { });
    private void OnPaneChanged(object? sender, PropertyChangedEventArgs args)
    {
        this.RaisePropertyChanged(nameof(CurrentPane));
        this.RaisePropertyChanged(nameof(ActivePane));
    }
    public void Dispose() { _pane.PropertyChanged -= OnPaneChanged; _pane.Dispose(); }
}

public sealed class EditorViewModel : ReactiveObject, IDocumentPane, IActivatableViewModel
{
    private string _title = "Untitled";
    private string _body = "";
    private string _savedMessage = "";
    private int _activationCount;
    private int _deactivationCount;

    public EditorViewModel(DocumentViewModel host)
    {
        HostScreen = host;
        SaveCommand = ReactiveCommand.CreateFromTask(async (CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(Title)) throw new ArgumentException("A note needs a title.");
            await Task.Delay(120, token);
            SavedMessage = $"Saved {Title}";
        });
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
    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }

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
