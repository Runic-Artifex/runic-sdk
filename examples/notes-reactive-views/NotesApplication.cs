using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;
using Runic.Application.Views.ReactiveUI;
using Splat;

namespace NotesReactiveViews;

public sealed class NotesApplication : IDisposable
{
    private readonly ServiceProvider _services;
    private NotesApplication(ServiceProvider services) => _services = services;

    public static NotesApplication Create()
    {
        AppLocator.CurrentMutable.RegisterConstant(new NullLogger(), typeof(ILogger));
        AppLocator.CurrentMutable.RegisterConstant(new DefaultLogManager(AppLocator.Current), typeof(ILogManager));
        var locator = new DefaultViewLocator();
        locator.CreateMappingBuilder()
            .Map<HomeViewModel>(() => new HomeView())
            .Map<DocumentViewModel>(() => new DocumentView())
            .Map<EditorViewModel>(() => new EditorView())
            .Map<EditorViewModel>(() => new CompactEditorView(), "compact")
            .Map<PreviewViewModel>(() => new PreviewView())
            .Map<PinnedNoteViewModel>(() => new PinnedNoteView())
            .Map<PinnedTaskViewModel>(() => new PinnedTaskView());
        var services = new ServiceCollection();
        // A window scope owns one execution lane for the whole reactive graph.
        // The Shell binds its children before the bridge can expose any of them.
        services.AddScoped<IRunicModelContext, RunicModelContext>();
        services.AddScoped<ShellViewModel>();
        services.AddScoped<IRunicViewLocator>(_ => new ReactiveRunicViewLocator(locator));
        services.AddRunicBridges();
        return new NotesApplication(services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
    }

    public NotesWindow OpenWindow() => _services.OpenWindow<NotesWindow, ShellViewModel>(host => new NotesWindow(host));
    public void Wait() => WebUiApplication.Wait();
    public void Dispose() { _services.Dispose(); WebUiApplication.Clean(); }
}
