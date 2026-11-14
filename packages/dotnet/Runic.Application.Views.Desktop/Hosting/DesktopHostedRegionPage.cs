using Runic.Application.Views;
using Runic.Desktop;

namespace Runic.Application.Views.Desktop.Hosting;

// The page of one of the host's own Windows as region mode sees it: the regions its Views connections declare
// (hosted-regions/1, BridgeHostedRegions) reach the host's region directory, and the directory's pushes reach the page.
// Created for each Window the host opens, released when its native window closes or, for a browser (serve-only) host,
// when the Window closes.
internal sealed class DesktopHostedRegionPage : IHostedRegionPage, IHostedRegionObserver, IDisposable
{
    private readonly HostedRegionDirectory _directory;
    private readonly BridgeHostedRegions _regions;
    // The origin a declaring connection's page has in its browser (DesktopBridgeTransport.ConnectionOrigin), or null.
    private readonly Func<string, string?>? _originOf;
    // The directory learns the page's regions in their order: the current ones first, then each event.
    private readonly Lock _gate = new();
    private IDisposable? _lease;
    private bool _closed;

    private DesktopHostedRegionPage(HostedRegionDirectory directory, BridgeHostedRegions regions, IRunicWindow window, DesktopWindow? nativeWindow,
        string embedder, bool browser, Func<string, string?>? originOf)
    {
        _directory = directory;
        _regions = regions;
        _originOf = originOf;
        Window = window;
        NativeWindow = nativeWindow;
        Embedder = embedder;
        IsBrowser = browser;
    }

    public IRunicWindow? Window { get; }

    public DesktopWindow? NativeWindow { get; }

    public string Embedder { get; }

    public bool IsBrowser { get; }

    // An embedded window's profile holds only the host's own cookies, so the guest may fall back to the same site; an
    // installed browser holds other local applications' too (host-protocol.md, Guest origin).
    public bool AllowsSameSite => !IsBrowser;

    // Follows the regions of the Window's page from now on, until its native window closes. The embedder of a region is
    // the origin of the page that declared it, as its browser sent it, such as a development server's, else the page's
    // surface origin.
    internal static DesktopHostedRegionPage Follow(HostedRegionDirectory directory, BridgeHostedRegions regions, IRunicWindow window,
        DesktopWindow nativeWindow, Uri pageUrl, Func<string, string?>? originOf = null)
    {
        var page = new DesktopHostedRegionPage(directory, regions, window, nativeWindow, Origin(pageUrl),
            nativeWindow.Browser != BrowserKind.Embedded, originOf);
        page.Start();
        nativeWindow.Closed += (_, _) => page.Dispose();
        return page;
    }

    // A browser (serve-only) host: the Window's page runs in the user's browser, and no native window shows it. Its
    // regions' guests serve their frames cross-site only, and the page ends when the Window's close starts.
    internal static DesktopHostedRegionPage FollowServed(HostedRegionDirectory directory, BridgeHostedRegions regions, IRunicWindow window,
        Uri pageUrl, Func<string, string?>? originOf, Task closed)
    {
        var page = new DesktopHostedRegionPage(directory, regions, window, nativeWindow: null, Origin(pageUrl), browser: true, originOf);
        page.Start();
        _ = closed.ContinueWith(_ => page.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return page;
    }

    private void Start()
    {
        lock (_gate)
        {
            _lease = _regions.Observe(this, out var current);
            foreach (var declaration in current)
                _directory.Declare(this, declaration.Name, declaration.Sizing, declaration.Report, EmbedderOf(declaration.ConnectionKey));
        }
    }

    // http://127.0.0.1:38200, as browsers serialize an origin.
    internal static string Origin(Uri url) => url.GetLeftPart(UriPartial.Authority);

    // The origin of the declaring connection's page, which the surface's origin check admitted, else the page's own.
    private string EmbedderOf(string connectionKey) => _originOf?.Invoke(connectionKey) is { Length: > 0 } origin ? origin : Embedder;

    public void Declared(HostedRegionDeclaration declaration)
    {
        lock (_gate)
        {
            if (_closed) return;
            _directory.Declare(this, declaration.Name, declaration.Sizing, declaration.Report, EmbedderOf(declaration.ConnectionKey));
        }
    }

    // The page removed the region (removed), or its connection closed or a later connection of the window declared the
    // name (reload): the producer cannot tell a reload from a crash.
    public void Removed(HostedRegionRemoval removal)
    {
        lock (_gate)
        {
            if (_closed) return;
            _directory.Remove(this, removal.Name, removal.ConnectionClosed || removal.Superseded ? "reload" : "removed");
        }
    }

    public void StateChanged(HostedRegionReport report)
    {
        lock (_gate)
        {
            if (_closed) return;
            _directory.Report(this, report);
        }
    }

    public bool ShowFrame(string name, string src, string? title, string allow, string? colorScheme = null) =>
        _regions.ShowFrame(name, src, title, allow, colorScheme);

    public bool ShowComponent(string name, string module, string url, string token, string? colorScheme = null) =>
        _regions.ShowComponent(name, module, url, token, colorScheme);

    public bool Update(string name, HostedRegionTitle title, int? height, HostedRegionColorScheme colorScheme = default) =>
        _regions.Update(name, title, height, colorScheme);

    public bool Empty(string name, HostedRegionEmptyReason reason) => _regions.Empty(name, reason);

    public bool Focus(string name) => _regions.Focus(name);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
        }
        _lease?.Dispose();
        _directory.PageClosed(this);
    }
}
