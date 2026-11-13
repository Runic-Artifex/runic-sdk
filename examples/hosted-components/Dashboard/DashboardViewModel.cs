using System.Collections.Concurrent;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Navigation;

namespace HostedDashboard;

// The dashboard: it starts two widgets, each shown in its region of the dashboard page, and shows how each region
// shows its widget.
public sealed class DashboardViewModel : ReactiveObject, IDisposable
{
    private readonly IRunicHostedApplications _applications;
    private readonly IRunicModelContext _context;
    private readonly DashboardOptions _options;
    private readonly ConcurrentDictionary<string, RunicHostedRegion> _regions = new();
    private readonly List<RunicHostedApplication> _widgets = [];
    private string _indexer = "stopped";
    private string _sync = "stopped";
    private string _scopedRegistries = "";

    public DashboardViewModel(IRunicHostedApplications applications, IRunicModelContext context, DashboardOptions options)
    {
        _applications = applications;
        _context = context;
        _options = options;
        _applications.WindowOpened += OnWindowOpened;
        ReportEngineCommand = ReactiveCommand.Create<string>(value => ScopedRegistries = value);
    }

    // How each region shows its widget: "starting", the region's state and its embedding, such as "attached, component",
    // or "failed: <code>".
    public string Indexer { get => _indexer; private set => this.RaiseAndSetIfChanged(ref _indexer, value); }

    public string Sync { get => _sync; private set => this.RaiseAndSetIfChanged(ref _sync, value); }

    // Whether the page's engine implements scoped custom element registries, as the page reports it: "supported" or
    // "unsupported". Component builds prefix their tags in every engine either way (README, "Tags").
    public string ScopedRegistries { get => _scopedRegistries; private set => this.RaiseAndSetIfChanged(ref _scopedRegistries, value); }

    public ReactiveCommand<string, RxVoid> ReportEngineCommand { get; }

    internal RunicHostedRegion? Region(string name) => _regions.GetValueOrDefault(name);

    internal IReadOnlyList<RunicHostedApplication> Widgets => _widgets;

    // Starts both widgets; their windows then open in the page's regions (WidgetPolicy).
    internal async Task StartWidgetsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (region, service) in Guests.Widgets)
        {
            Set(region, "starting");
            try
            {
                _widgets.Add(await _applications.StartAsync(Guests.Widget(service, _options.Elevated),
                    new RunicHostedApplicationOptions { HostName = "Hosted components dashboard", Policy = new WidgetPolicy(region) },
                    cancellationToken));
            }
            catch (RunicHostedApplicationException error)
            {
                Set(region, $"failed: {error.Code}");
            }
        }
    }

    private void OnWindowOpened(object? sender, RunicHostedWindowEventArgs e)
    {
        if (e.Window.Region is not { } region) return;
        _regions[region.Name] = region;
        // IsComponent: the decision was EmbedComponent. The region still shows the widget in no frame when the component
        // cannot load, such as under a policy without the widget's origin: it then stays loading or detached.
        void Show() => Set(region.Name, $"{region.State.ToString().ToLowerInvariant()}, {(region.IsComponent ? "component" : "frame")}");
        region.StateChanged += (_, _) => Show();
        Show();
    }

    private void Set(string region, string state) => _context.TryPost(() =>
    {
        if (region == "indexer") Indexer = state;
        else Sync = state;
    });

    public void Dispose()
    {
        _applications.WindowOpened -= OnWindowOpened;
        ReportEngineCommand.Dispose();
    }
}
