using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi.DependencyInjection;
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
        var locator = new DefaultViewLocator()
            .Map<HomeViewModel, HomeView>(() => new HomeView())
            .Map<DocumentViewModel, DocumentView>(() => new DocumentView())
            .Map<EditorViewModel, EditorView>(() => new EditorView())
            .Map<EditorViewModel, CompactEditorView>(() => new CompactEditorView(), "compact")
            .Map<PreviewViewModel, PreviewView>(() => new PreviewView());
        var services = new ServiceCollection();
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
