using Runic.Application.Views;
using Runic.Desktop;

namespace Runic.Application.Views.Desktop.Hosting;

// The page of one of the host's own Windows as region mode sees it: the regions its Views connections declare
// (hosted-regions/1, BridgeHostedRegions) reach the host's region directory, and the directory's pushes reach the page.
// Created for each Window the host opens, released when its native window closes.
internal sealed class DesktopHostedRegionPage : IHostedRegionPage, IHostedRegionObserver, IDisposable
{
    private readonly HostedRegionDirectory _directory;
    private readonly BridgeHostedRegions _regions;
    // The directory learns the page's regions in their order: the current ones first, then each event.
    private readonly Lock _gate = new();
    private IDisposable? _lease;
    private bool _closed;

    private DesktopHostedRegionPage(HostedRegionDirectory directory, BridgeHostedRegions regions, IRunicWindow window, DesktopWindow nativeWindow,
        string embedder, bool allowsSameSite)
    {
        _directory = directory;
        _regions = regions;
        Window = window;
        NativeWindow = nativeWindow;
        Embedder = embedder;
        AllowsSameSite = allowsSameSite;
    }

    public IRunicWindow? Window { get; }

    public DesktopWindow? NativeWindow { get; }

    public string Embedder { get; }

    public bool AllowsSameSite { get; }

    // Follows the regions of the Window's page from now on, until its native window closes.
    internal static DesktopHostedRegionPage Follow(HostedRegionDirectory directory, BridgeHostedRegions regions, IRunicWindow window,
        DesktopWindow nativeWindow, Uri pageUrl)
    {
        // The embedder is the page's origin. An embedded window's profile holds only the host's own cookies, so the guest
        // may fall back to the same site; an installed browser holds other local applications' too (host-protocol.md,
        // Guest origin).
        var page = new DesktopHostedRegionPage(directory, regions, window, nativeWindow, Origin(pageUrl),
            nativeWindow.Browser == BrowserKind.Embedded);
        lock (page._gate)
        {
            page._lease = regions.Observe(page, out var current);
            foreach (var declaration in current) directory.Declare(page, declaration.Name, declaration.Sizing, declaration.Report);
        }
        nativeWindow.Closed += (_, _) => page.Dispose();
        return page;
    }

    // http://127.0.0.1:38200, as browsers serialize an origin.
    internal static string Origin(Uri url) => url.GetLeftPart(UriPartial.Authority);

    public void Declared(HostedRegionDeclaration declaration)
    {
        lock (_gate)
        {
            if (_closed) return;
            _directory.Declare(this, declaration.Name, declaration.Sizing, declaration.Report);
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

    public bool ShowFrame(string name, string src, string? title, string allow) => _regions.ShowFrame(name, src, title, allow);

    public bool ShowComponent(string name, string module, string url, string token) => _regions.ShowComponent(name, module, url, token);

    public bool Update(string name, HostedRegionTitle title, int? height) => _regions.Update(name, title, height);

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
