using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Navigation;
using Runic.Platform;

namespace DocumentTabs;

/// <summary>A page that a document tab presents.</summary>
public interface IDocumentPage;

/// <summary>The Window's ViewModel: the open documents, one tab each.</summary>
public sealed partial class DocumentsViewModel : ObservableObject
{
    /// <summary>The URL scheme of the example's deep links, such as <c>document-tabs://documents/2/info</c>.</summary>
    public const string LinkScheme = "document-tabs";

    private readonly RunicNavigator _navigator;
    private readonly DocumentStore _store;
    private readonly NavigationRouteMap _routes;
    private readonly NavigationStateFile _state;
    private readonly object _saving = new();
    private int _saveRequested;

    public DocumentsViewModel(RunicNavigator navigator, DocumentStore store, NavigationRouteMap routes, NavigationStateFile state)
    {
        _navigator = navigator;
        _store = store;
        _routes = routes;
        _state = state;
        // RestoreRegion gives a restored path the empty tabs it restores into.
        Documents = navigator.CreateSelection(this, [],
            new NavigationSelectionOptions { RestoreRegion = _ => navigator.CreateRegion<IDocumentPage>(this) });
        // The tabs are saved after every change, so they reopen after a restart however the window or process ended.
        Documents.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavigationSelection.Regions))
                foreach (var tab in Documents.Regions.Where(_watched.Add)) tab.PropertyChanged += OnTabChanged;
            if (e.PropertyName is nameof(NavigationSelection.Regions) or nameof(NavigationSelection.SelectedIndex)) RequestSave();
        };
    }

    private readonly HashSet<NavigationRegion> _watched = [];

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavigationRegion.CurrentEntry)) RequestSave();
    }

    /// <summary>
    /// The open documents. Each tab is a navigation region with its own back stack. The page selects and closes tabs
    /// by key; a close asks every page of the tab to retire.
    /// </summary>
    [RunicRegionCollection(typeof(IDocumentPage))]
    public NavigationSelection Documents { get; }

    /// <summary>Opens a new document in a tab of its own and selects it.</summary>
    [RelayCommand]
    private Task NewDocumentAsync() => OpenTabAsync($"documents/{_store.Create()}");

    /// <summary>
    /// Opens a deep link, such as <c>document-tabs://documents/2/info</c> from the command line, a second instance or a
    /// notification: the tab of that document, or a new one, with the document and its details page.
    /// </summary>
    /// <returns><see langword="false"/> for a link that names no route or no stored document.</returns>
    public async Task<bool> OpenLinkAsync(Uri link)
    {
        // The link is untrusted: only the map's typed routes open, and only documents the store knows.
        if (!NavigationRouteLinks.TryGetRoute(link, [LinkScheme], out var route)
            || !_routes.TryMatch(route, out var canonical, out var values)
            || !values.TryGetValue("id", out var id) || !_store.Exists((int)id))
            return false;
        // The tab whose bottom page is that document.
        var open = Documents.Regions.FirstOrDefault(tab => (tab.History.Count > 0 ? tab.History[0] : tab.CurrentEntry)?.Content
            is DocumentViewModel document && document.Id == (int)id);
        if (open is null) return await OpenTabAsync(canonical);
        // Stack keeps the document and goes back to it, or pushes its details page.
        var opened = await open.OpenRouteAsync(canonical, NavigationRouteOpen.Stack);
        return opened?.IsCommitted == true && (await Documents.SelectAsync(open)).IsCommitted;
    }

    /// <summary>Opens a notification's activation link (<see cref="DesktopNotification.ActivationUri"/>).</summary>
    public Task<bool> OpenNotificationAsync(DesktopNotificationActivation activation) =>
        activation.ActivationUri is { } link ? OpenLinkAsync(link) : Task.FromResult(false);

    /// <summary>Saves the open tabs and their pages, to reopen them after a restart.</summary>
    public void SaveNavigation()
    {
        lock (_saving)
        {
            // A closing window's navigator retires its tabs; that is not a state to reopen.
            if (_navigator.IsClosed) return;
            _state.Write(_navigator.GetPath(_routes));
        }
    }

    // Navigation changes are raised in model turns: save shortly after, on the thread pool, once for a burst of them.
    private void RequestSave()
    {
        if (Interlocked.Exchange(ref _saveRequested, 1) == 0)
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                Volatile.Write(ref _saveRequested, 0);
                SaveNavigation();
            });
    }

    /// <summary>Reopens the tabs <see cref="SaveNavigation"/> saved, through the pages' guards and hooks.</summary>
    public async Task<NavigationRestoreResult> RestoreNavigationAsync()
    {
        var path = _state.Read();
        return path is null ? new NavigationRestoreResult([]) : await _navigator.RestoreAsync(path, _routes);
    }

    private async Task<bool> OpenTabAsync(string route)
    {
        var tab = _navigator.CreateRegion<IDocumentPage>(this);
        // The tab owns its pages: closing the tab disposes them.
        var opened = await tab.OpenRouteAsync(route, NavigationRouteOpen.Stack, _routes);
        if (opened?.IsCommitted != true) return false;
        await _navigator.ModelContext.InvokeAsync(() => Documents.Add(tab));
        return (await Documents.SelectAsync(tab)).IsCommitted;
    }

    // The tab whose current page is the document.
    internal NavigationRegion<IDocumentPage>? TabOf(DocumentViewModel document) =>
        Documents.Regions.OfType<NavigationRegion<IDocumentPage>>().FirstOrDefault(tab => ReferenceEquals(tab.Current, document));
}

/// <summary>A document, the first page of its tab: <c>documents/{id}</c>.</summary>
public sealed partial class DocumentViewModel(DocumentStore store, OpenDocuments open, DocumentsViewModel documents)
    : ObservableObject, IDocumentPage, INavigationDepartureGuard, INavigationInitialize<int>, IDisposable
{
    private string _savedTitle = "";
    private string _savedBody = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    private string title = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    private string body = "";

    private int _saves;

    /// <summary>The document's id in the store.</summary>
    public int Id { get; private set; }

    /// <summary>Whether the title or body differ from the saved document.</summary>
    public bool IsDirty => Title != _savedTitle || Body != _savedBody;

    /// <summary>How often the document was saved.</summary>
    public int Saves
    {
        get => _saves;
        private set => SetProperty(ref _saves, value);
    }

    // The route's id: the navigator passes it before the page is presented.
    ValueTask INavigationInitialize<int>.InitializeAsync(NavigationEntryContext entry, int id, CancellationToken cancellationToken)
    {
        Id = id;
        (_savedTitle, _savedBody) = store.Get(id);
        Title = _savedTitle;
        Body = _savedBody;
        open.Add(this);
        return ValueTask.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Save()
    {
        _savedTitle = Title;
        _savedBody = Body;
        store.Save(Id, Title, Body);
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

    /// <summary>Shows the document's details on a second page of the same tab: <c>documents/{id}/info</c>.</summary>
    [RelayCommand]
    private Task ShowDetails() => documents.TabOf(this) is { } tab
        // Expecting this entry to be current makes a second click a no-op instead of a second page.
        ? tab.PushAsync<DocumentInfoViewModel, int>(Id, new NavigationRequestOptions(tab.CurrentEntry?.Id)).AsTask()
        : Task.CompletedTask;

    // Switching to another tab or showing the details keeps the document (Retain), so it is never blocked. Closing the
    // tab retires it, which is refused while the document has unsaved changes.
    ValueTask<bool> INavigationDepartureGuard.CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        ValueTask.FromResult(departure.Kind == NavigationDepartureKind.Retain || departure.ContentContinues || !IsDirty);

    // The tab created this page, so it disposes it when the tab closes.
    public void Dispose() => open.Remove(this);

    private void Settled()
    {
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>The details of a document, pushed over it in its tab: <c>documents/{id}/info</c>.</summary>
public sealed partial class DocumentInfoViewModel(OpenDocuments open) : ObservableObject, IDocumentPage, INavigationInitialize<int>, IDisposable
{
    private NavigationEntryContext? _entry;
    private DocumentViewModel? _document;

    public string Title => _document?.Title ?? "";
    public int Words => _document?.Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
    public int Characters => _document?.Body.Length ?? 0;
    public int Saves => _document?.Saves ?? 0;
    public bool IsDirty => _document?.IsDirty ?? false;

    // The route's id names the document below this page; a link to the details of a closed document opens it first
    // (NavigationRouteOpen.Stack), so the document is open by now.
    ValueTask INavigationInitialize<int>.InitializeAsync(NavigationEntryContext entry, int id, CancellationToken cancellationToken)
    {
        _entry = entry;
        _document = open.Find(id) ?? throw new InvalidOperationException($"Document {id} is not open.");
        _document.PropertyChanged += OnDocumentChanged;
        return ValueTask.CompletedTask;
    }

    /// <summary>Goes back to the document in the same tab.</summary>
    [RelayCommand]
    private Task Back() => _entry?.BackAsync().AsTask() ?? Task.CompletedTask;

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
    public void Dispose()
    {
        if (_document is not null) _document.PropertyChanged -= OnDocumentChanged;
    }
}

/// <summary>The stored documents. The example keeps them in memory; an application reads them from its own store.</summary>
public sealed class DocumentStore
{
    private readonly Dictionary<int, (string Title, string Body)> _documents = [];
    private int _next;

    public int Create()
    {
        lock (_documents)
        {
            var id = ++_next;
            _documents[id] = ($"Untitled {id}", "");
            return id;
        }
    }

    public bool Exists(int id)
    {
        lock (_documents) return _documents.ContainsKey(id);
    }

    // A document of a restored tab that this process doesn't have yet starts as a new one with that id.
    public (string Title, string Body) Get(int id)
    {
        lock (_documents)
        {
            if (_documents.TryGetValue(id, out var document)) return document;
            _next = Math.Max(_next, id);
            return _documents[id] = ($"Untitled {id}", "");
        }
    }

    public void Save(int id, string title, string body)
    {
        lock (_documents) _documents[id] = (title, body);
    }
}

/// <summary>The documents open in a window, by id, so a details page finds the document below it.</summary>
public sealed class OpenDocuments
{
    private readonly Dictionary<int, DocumentViewModel> _open = [];

    public DocumentViewModel? Find(int id)
    {
        lock (_open) return _open.GetValueOrDefault(id);
    }

    internal void Add(DocumentViewModel document)
    {
        lock (_open) _open[document.Id] = document;
    }

    internal void Remove(DocumentViewModel document)
    {
        lock (_open)
            if (_open.TryGetValue(document.Id, out var current) && ReferenceEquals(current, document)) _open.Remove(document.Id);
    }
}

/// <summary>
/// Where the window's navigation path is kept between runs: a JSON file next to the application's other state. The SDK
/// keeps no settings itself; any store works.
/// </summary>
public sealed class NavigationStateFile(string? path)
{
    public NavigationPath? Read()
    {
        if (path is null || !File.Exists(path)) return null;
        try { return NavigationPath.FromJson(File.ReadAllText(path)); }
        catch (FormatException) { return null; } // a damaged file starts with no tabs
    }

    public void Write(NavigationPath navigation)
    {
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Replaced in one step, so a reader never sees half a file.
        var written = path + ".tmp";
        File.WriteAllText(written, navigation.ToJson());
        File.Move(written, path, overwrite: true);
    }
}

/// <summary>Registers the example's services.</summary>
public static class DocumentTabsServices
{
    /// <param name="services">The services.</param>
    /// <param name="stateFile">The file the window's tabs are saved to when it closes, or null to save nothing.</param>
    public static IServiceCollection AddDocumentTabs(this IServiceCollection services, string? stateFile = null)
    {
        // One model context and one navigator per Window scope.
        services.AddRunicNavigation();
        // The pages' routes: saved paths and deep links name pages by them.
        services.AddRunicNavigationRoutes(routes => routes
            .Map<DocumentViewModel, int>("documents/{id:int}")
            .Map<DocumentInfoViewModel, int>("documents/{id:int}/info"));
        services.AddSingleton<DocumentStore>();
        services.AddSingleton(new NavigationStateFile(stateFile));
        services.AddScoped<OpenDocuments>();
        services.AddScoped<DocumentsViewModel>();
        return services;
    }
}
