using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Navigation;

namespace DocumentTabs;

/// <summary>A page that a document tab presents.</summary>
public interface IDocumentPage;

/// <summary>The Window's ViewModel: the open documents, one tab each.</summary>
public sealed partial class DocumentsViewModel : ObservableObject
{
    private readonly RunicNavigator _navigator;
    private int _created;

    public DocumentsViewModel(RunicNavigator navigator)
    {
        _navigator = navigator;
        Documents = navigator.CreateSelection(this, []);
    }

    /// <summary>
    /// The open documents. Each tab is a navigation region with its own back stack. The page selects and closes tabs
    /// by key; a close asks every page of the tab to retire.
    /// </summary>
    [RunicRegionCollection(typeof(IDocumentPage))]
    public NavigationSelection Documents { get; }

    /// <summary>Opens a new document in a tab of its own and selects it.</summary>
    [RelayCommand]
    private async Task NewDocumentAsync()
    {
        var tab = _navigator.CreateRegion<IDocumentPage>(this);
        // The tab owns the document: closing the tab disposes it.
        await tab.PushAsync(NavigationTarget.Own<IDocumentPage>(new DocumentViewModel(tab, $"Untitled {++_created}")));
        Documents.Add(tab);
        await Documents.SelectAsync(tab);
    }
}

/// <summary>A document, the first page of its tab.</summary>
public sealed partial class DocumentViewModel : ObservableObject, IDocumentPage, INavigationDepartureGuard
{
    private readonly NavigationRegion<IDocumentPage> _tab;
    private string _savedTitle;
    private string _savedBody = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    private string title;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    private string body = "";

    private int _saves;

    public DocumentViewModel(NavigationRegion<IDocumentPage> tab, string title)
    {
        _tab = tab;
        this.title = title;
        _savedTitle = title;
    }

    /// <summary>Whether the title or body differ from the saved document.</summary>
    public bool IsDirty => Title != _savedTitle || Body != _savedBody;

    /// <summary>How often the document was saved.</summary>
    public int Saves
    {
        get => _saves;
        private set => SetProperty(ref _saves, value);
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Save()
    {
        _savedTitle = Title;
        _savedBody = Body;
        Saves++;
        Settled();
    }

    /// <summary>Returns to the saved title and body.</summary>
    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Discard()
    {
        Title = _savedTitle;
        Body = _savedBody;
        Settled();
    }

    /// <summary>Shows the document's details on a second page of the same tab.</summary>
    [RelayCommand]
    private Task ShowDetails() => ReferenceEquals(_tab.Current, this)
        // Expecting this entry to be current makes a second click a no-op instead of a second page.
        ? _tab.PushAsync(NavigationTarget.Create<IDocumentPage>((_, entry) => new DocumentInfoViewModel(entry, this)),
            new NavigationRequestOptions(_tab.CurrentEntry?.Id)).AsTask()
        : Task.CompletedTask;

    // Switching to another tab or showing the details keeps the document (Retain), so it is never blocked. Closing the
    // tab retires it, which is refused while the document has unsaved changes.
    ValueTask<bool> INavigationDepartureGuard.CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        ValueTask.FromResult(departure.Kind == NavigationDepartureKind.Retain || departure.ContentContinues || !IsDirty);

    private void Settled()
    {
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>The details of a document, pushed over it in its tab.</summary>
public sealed partial class DocumentInfoViewModel : ObservableObject, IDocumentPage, IDisposable
{
    private readonly NavigationEntryContext _entry;
    private readonly DocumentViewModel _document;

    public DocumentInfoViewModel(NavigationEntryContext entry, DocumentViewModel document)
    {
        _entry = entry;
        _document = document;
        _document.PropertyChanged += OnDocumentChanged;
    }

    public string Title => _document.Title;
    public int Words => _document.Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    public int Characters => _document.Body.Length;
    public int Saves => _document.Saves;
    public bool IsDirty => _document.IsDirty;

    /// <summary>Goes back to the document in the same tab.</summary>
    [RelayCommand]
    private Task Back() => _entry.BackAsync().AsTask();

    // The document changes inside model turns, so these notifications are raised in them too.
    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.Title):
            case nameof(DocumentViewModel.Saves):
            case nameof(DocumentViewModel.IsDirty):
                OnPropertyChanged(e.PropertyName);
                break;
            case nameof(DocumentViewModel.Body):
                OnPropertyChanged(nameof(Words));
                OnPropertyChanged(nameof(Characters));
                break;
        }
    }

    // The tab created this page, so it disposes it when Back retires it or the tab closes.
    public void Dispose() => _document.PropertyChanged -= OnDocumentChanged;
}

/// <summary>Registers the example's services.</summary>
public static class DocumentTabsServices
{
    public static IServiceCollection AddDocumentTabs(this IServiceCollection services)
    {
        // One model context and one navigator per Window scope.
        services.AddRunicNavigation();
        services.AddScoped<DocumentsViewModel>();
        return services;
    }
}
