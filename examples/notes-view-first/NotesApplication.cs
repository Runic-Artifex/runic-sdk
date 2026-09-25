using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi.DependencyInjection;
using Splat;

namespace NotesWindowViews;

public sealed class NotesApplication : IDisposable
{
    private readonly ServiceProvider _services;

    private NotesApplication(ServiceProvider services) => _services = services;

    internal IServiceProvider Services => _services;

    public static NotesApplication Create(bool useSplat)
    {
        var services = new ServiceCollection();
        services.AddScoped<INotesStorage, MemoryNotesStorage>();
        services.AddScoped<HomeViewModel>();
        services.AddScoped<EditorViewModel>();
        services.AddScoped<PreviewViewModel>();
        services.AddScoped<DocumentViewModel>();
        services.AddScoped<WorkspaceNavigation>();
        services.AddScoped<SidebarViewModel>();
        services.AddScoped<ShellViewModel>();
        services.AddRunicBridges();

        if (useSplat)
        {
            RegisterSplatViews();
            services.AddScoped<IRunicViewLocator, SplatViewLocator>();
            if (AppLocator.Current.GetService<IViewFor<EditorViewModel>>() is not EditorView)
                throw new InvalidOperationException("Splat did not register EditorView as IViewFor<EditorViewModel>.");
        }
        else
        {
            services.AddTransient<SidebarView>();
            services.AddTransient<HomeView>();
            services.AddTransient<DocumentView>();
            services.AddTransient<EditorView>();
            services.AddTransient<PreviewView>();
            services.AddTransient<ConfirmNavigationView>();
            services.AddScoped<IRunicViewLocator, MicrosoftViewLocator>();
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

public sealed class MicrosoftViewLocator(IServiceProvider services) : IRunicViewLocator
{
    public TView Locate<TView, TViewModel>()
        where TView : class, IRunicView
        where TViewModel : class => services.GetRequiredService<TView>();
}

public sealed class SplatViewLocator : IRunicViewLocator
{
    public TView Locate<TView, TViewModel>()
        where TView : class, IRunicView
        where TViewModel : class =>
        ReactiveUI.ViewLocator.Current.ResolveView<TViewModel>() as TView
            ?? throw new InvalidOperationException($"ReactiveUI could not locate {typeof(TView).Name} for {typeof(TViewModel).Name}.");
}
