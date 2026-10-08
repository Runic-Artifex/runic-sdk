using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Runic.Navigation;

namespace HybridNotes;

public static class ModelRegistration
{
    public static IServiceCollection AddHybridNotes(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<INoteStore, NoteStore>();
        services.AddScoped<AppRegions>();
        return services;
    }
}

public sealed class AppRegions
{
    public AppRegions(RunicNavigator navigator)
    {
        Main = navigator.CreateRegion<object>(this);
        Dialog = navigator.CreateRegion<object>(this);
    }

    public NavigationRegion<object> Main { get; }
    public NavigationRegion<object> Dialog { get; }
}

public sealed class NotesListViewModel : ObservableObject, INavigationResume
{
    private readonly INoteStore _store;

    public NotesListViewModel(INoteStore store, AppRegions regions)
    {
        _store = store;
        Notes = new(store.All);
        OpenCommand = new AsyncRelayCommand<Note>((note, token) => regions.Main
            .PushAsync<EditorViewModel, int>(note!.Id, cancellationToken: token).AsTask(), note => note is not null);
    }

    public ObservableCollection<Note> Notes { get; }
    public IAsyncRelayCommand<Note> OpenCommand { get; }

    public ValueTask ResumeAsync(NavigationResume resume, CancellationToken token)
    {
        Notes.ReplaceWith(_store.All);
        return ValueTask.CompletedTask;
    }
}

public sealed class ConfirmViewModel : ObservableObject, INavigationInitialize<string>
{
    private NavigationEntryContext? _entry;

    public ConfirmViewModel()
    {
        YesCommand = new AsyncRelayCommand(() => _entry!.CompleteAsync(true).AsTask());
        NoCommand = new AsyncRelayCommand(() => _entry!.DismissAsync().AsTask());
    }

    public string Message { get; private set; } = "";
    public IAsyncRelayCommand YesCommand { get; }
    public IAsyncRelayCommand NoCommand { get; }

    public ValueTask InitializeAsync(NavigationEntryContext entry, string message, CancellationToken token)
    {
        _entry = entry;
        Message = message;
        return ValueTask.CompletedTask;
    }
}
