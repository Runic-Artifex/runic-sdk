using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;
using Runic.Application.Views.ReactiveUI;
using Microsoft.Extensions.Logging;
using Runic.Navigation;
using Splat;

namespace NotesReactiveViews;

public sealed class NotesApplication : IDisposable
{
    private readonly ServiceProvider _services;
    private NotesApplication(ServiceProvider services) => _services = services;

    public static NotesApplication Create()
    {
        AppLocator.CurrentMutable.RegisterConstant(new NullLogger(), typeof(Splat.ILogger));
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
        // Views diagnostics, such as a routed region that cannot present its route,
        // go to the console; without a logger factory they would go to Trace.
        services.AddLogging(logging => logging.AddSimpleConsole().SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));
        // A window scope owns one execution lane for the whole reactive graph, and
        // ReactiveUI commands deliver their notifications on it without a scheduler.
        services.AddRunicReactiveModelContext();
        // One navigator per window scope, on that model context. It constructs each
        // DocumentViewModel from the window's services and owns it.
        services.AddRunicNavigation();
        services.AddScoped<HomeViewModel>();
        services.AddScoped<EditorViewModel>();
        services.AddScoped<PreviewViewModel>();
        services.AddScoped<ShellViewModel>();
        services.AddScoped<IRunicViewLocator>(_ => new ReactiveRunicViewLocator(locator));
        services.AddRunicBridges();
        return new NotesApplication(services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
    }

    // Opens a Notes window in its own scope. Without show, the caller shows it or starts its server.
    public ValueTask<NotesWindow> OpenWindowAsync(string webRoot, int width, int height, ushort? port = null, bool show = true) =>
        new RunicCsWebUiHost(_services, new RunicCsWebUiHostOptions
        {
            ConfigureWindow = port is { } value ? native => native.SetPort(value) : null,
            ShowWindow = show ? null : static (_, _) => { },
        }).OpenWindowAsync<NotesWindow>(new RunicWindowOptions { ContentRoot = webRoot, Width = width, Height = height });
    public void Dispose() { _services.Dispose(); WebUiApplication.Clean(); }
}
