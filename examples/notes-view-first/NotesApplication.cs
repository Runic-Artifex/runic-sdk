using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReactiveUI;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;
using Splat;

namespace NotesWindowViews;

public sealed class NotesApplication : IDisposable
{
    private readonly ServiceProvider _services;

    private NotesApplication(ServiceProvider services) => _services = services;

    internal IServiceProvider Services => _services;

    public static NotesApplication Create(bool useSplat)
    {
        var services = new ServiceCollection().AddNotes();

        if (useSplat)
        {
            services.AddRunicBridges();
            RegisterSplatViews();
            services.AddScoped<IRunicViewLocator, SplatViewLocator>();
            if (AppLocator.Current.GetService<IViewFor<EditorViewModel>>() is not EditorView)
                throw new InvalidOperationException("Splat did not register EditorView as IViewFor<EditorViewModel>.");
        }
        else
        {
            // Registers the Bridges, every View as transient, and the
            // service-provider View locator.
            services.AddRunicViews();
        }

        return new NotesApplication(services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
    }

    public NotesWindow OpenWindow() => _services.OpenWindow<NotesWindow, ShellViewModel>(host => new NotesWindow(host));

    public void Wait() => WebUiApplication.Wait();

    public void Dispose()
    {
        _services.Dispose();
        WebUiApplication.Clean();
    }

    private static void RegisterSplatViews()
    {
        AppLocator.CurrentMutable.RegisterConstant(new NullLogger(), typeof(ILogger));
        AppLocator.CurrentMutable.RegisterConstant(new DefaultLogManager(AppLocator.Current), typeof(ILogManager));
        AppLocator.CurrentMutable.RegisterConstant(new DefaultViewLocator(), typeof(IViewLocator));
        Register<SidebarView, SidebarViewModel>();
        Register<HomeView, HomeViewModel>();
        Register<DocumentView, DocumentViewModel>();
        Register<EditorView, EditorViewModel>();
        Register<PreviewView, PreviewViewModel>();
        Register<ConfirmNavigationView, ConfirmNavigationViewModel>();
    }

    private static void Register<TView, TViewModel>()
        where TView : ReactiveRunicView<TViewModel>, new()
        where TViewModel : class
    {
        AppLocator.CurrentMutable.Register(() => new TView(), typeof(IViewFor<TViewModel>));
    }
}

/// <summary>Registers the notes services and window-scoped ViewModels.</summary>
public static class NotesServices
{
    // Tests call this too, with their own clock and storage registered after it.
    public static IServiceCollection AddNotes(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        // One model context and one navigator per window scope; the window session shares the context.
        services.AddRunicNavigation();
        services.AddScoped<INotesStorage, MemoryNotesStorage>();
        services.AddScoped<NotesLibrary>();
        services.AddScoped<HomeViewModel>();
        services.AddScoped<EditorViewModel>();
        services.AddScoped<PreviewViewModel>();
        services.AddScoped<WorkspaceNavigation>();
        services.AddScoped<SidebarViewModel>();
        services.AddScoped<ShellViewModel>();
        return services;
    }
}

public sealed class SplatViewLocator : IRunicViewLocator
{
    public TView Locate<TView, TViewModel>()
        where TView : class, IRunicView
        where TViewModel : class =>
        ReactiveUI.Binding.ViewLocator.GetCurrent().ResolveView<TViewModel>() as TView
            ?? throw new InvalidOperationException($"ReactiveUI could not locate {typeof(TView).Name} for {typeof(TViewModel).Name}.");
}
