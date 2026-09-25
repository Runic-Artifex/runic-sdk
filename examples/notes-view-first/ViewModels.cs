using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NotesWindowViews;

// These interfaces describe independently presentable regions. They are not a
// second visual tree: the frontend decides where and whether to mount them.
public interface IMainViewModel { }
public interface IDocumentPaneViewModel { }
public interface IDialogViewModel { }

public sealed class WorkspaceNavigation(HomeViewModel home, DocumentViewModel document)
    : ObservableObject
{
    private IMainViewModel _main = home;
    private IDialogViewModel? _dialog;

    public IMainViewModel Main
    {
        get => _main;
        private set => SetProperty(ref _main, value);
    }

    public IDialogViewModel? Dialog
    {
        get => _dialog;
        private set => SetProperty(ref _dialog, value);
    }

    public void OpenDocument() => Main = document;

    public void OpenHome()
    {
        if (ReferenceEquals(Main, document) && document.Editor.IsDirty)
        {
            // This is an in-page modal. The coordinator owns the transient
            // ViewModel; closing it clears the Shell's dialog outlet.
            Dialog ??= new ConfirmNavigationViewModel(
                "Discard the unsaved edits and return Home?",
                () => { document.Editor.DiscardChanges(); Dialog = null; Main = home; },
                () => Dialog = null);
            return;
        }
        Main = home;
    }
}

public partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceNavigation _navigation;

    public ShellViewModel(SidebarViewModel sidebar, WorkspaceNavigation navigation)
    {
        Sidebar = sidebar;
        _navigation = navigation;
        _navigation.PropertyChanged += OnNavigationChanged;
    }

    public SidebarViewModel Sidebar { get; }
    public IMainViewModel Main => _navigation.Main;
    public IDialogViewModel? Dialog => _navigation.Dialog;

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceNavigation.Main) or nameof(WorkspaceNavigation.Dialog))
            OnPropertyChanged(e.PropertyName);
    }

    public void Dispose() => _navigation.PropertyChanged -= OnNavigationChanged;
}

public partial class SidebarViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceNavigation _navigation;

    public SidebarViewModel(WorkspaceNavigation navigation)
    {
        _navigation = navigation;
        _navigation.PropertyChanged += OnNavigationChanged;
    }

    public string Selected => _navigation.Main is DocumentViewModel ? "Notes" : "Home";

    [RelayCommand]
    private void OpenHome() => _navigation.OpenHome();

    [RelayCommand]
    private void OpenNotes() => _navigation.OpenDocument();

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceNavigation.Main)) OnPropertyChanged(nameof(Selected));
    }

    public void Dispose() => _navigation.PropertyChanged -= OnNavigationChanged;
}

public partial class HomeViewModel : ObservableObject, IMainViewModel
{
    public string Greeting => "Welcome to composed Notes";
}

public partial class DocumentViewModel : ObservableObject, IMainViewModel
{
    private readonly PreviewViewModel _preview;
    private IDocumentPaneViewModel _currentPane;

    public DocumentViewModel(EditorViewModel editor, PreviewViewModel preview)
    {
        Editor = editor;
        _preview = preview;
        _currentPane = editor;
    }

    internal EditorViewModel Editor { get; }

    public IDocumentPaneViewModel CurrentPane
    {
        get => _currentPane;
        private set
        {
            if (SetProperty(ref _currentPane, value)) OnPropertyChanged(nameof(ActivePane));
        }
    }

    public string ActivePane => ReferenceEquals(CurrentPane, Editor) ? "Editor" : "Preview";

    [RelayCommand]
    private void ShowEditor() => CurrentPane = Editor;

    [RelayCommand]
    private void ShowPreview() => CurrentPane = _preview;
}

public partial class EditorViewModel(INotesStorage storage) : ObservableObject, IDocumentPaneViewModel
{
    [ObservableProperty] private string title = "Untitled";
    [ObservableProperty] private string body = "";
    private bool _isDirty;
    private string _savedMessage = "";
    private string _savedTitle = "Untitled";
    private string _savedBody = "";

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public string SavedMessage
    {
        get => _savedMessage;
        private set => SetProperty(ref _savedMessage, value);
    }

    partial void OnTitleChanged(string value) => IsDirty = true;
    partial void OnBodyChanged(string value) => IsDirty = true;

    internal void DiscardChanges()
    {
        Title = _savedTitle;
        Body = _savedBody;
        IsDirty = false;
        SavedMessage = "Changes discarded.";
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken token)
    {
        var title = Title;
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A note needs a title.");
        var body = Body;
        await storage.SaveAsync(title, body, token);
        _savedTitle = title;
        _savedBody = body;
        IsDirty = Title != title || Body != body;
        SavedMessage = $"Saved {title}";
    }
}

public partial class PreviewViewModel : ObservableObject, IDocumentPaneViewModel, IDisposable
{
    private readonly EditorViewModel _editor;

    public PreviewViewModel(EditorViewModel editor)
    {
        _editor = editor;
        _editor.PropertyChanged += OnEditorChanged;
    }

    public string Heading => _editor.Title;
    public string Excerpt => string.IsNullOrWhiteSpace(_editor.Body) ? "Nothing written yet." : _editor.Body;

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.Title)) OnPropertyChanged(nameof(Heading));
        if (e.PropertyName == nameof(EditorViewModel.Body)) OnPropertyChanged(nameof(Excerpt));
    }

    public void Dispose() => _editor.PropertyChanged -= OnEditorChanged;
}

public partial class ConfirmNavigationViewModel(
    string message, Action confirm, Action cancel) : ObservableObject, IDialogViewModel
{
    public string Message => message;

    [RelayCommand]
    private void Confirm() => confirm();

    [RelayCommand]
    private void Cancel() => cancel();
}
