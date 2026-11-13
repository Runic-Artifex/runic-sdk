using Runic.Application.Views;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;

// A host page that the tests play (region/1, hosted-regions/1): it declares regions, reports their size, visibility
// and focus to the host's region directory, and records what the host shows in each. Like Runic's element, it moves the
// focus into a region at the host's request only while the page holds no focus of its own, and then reports the focus
// as the script's, a little later, as a page does after its next animation frame.
// A browser page (browser) stands for a browser (serve-only) host's page, which no native window shows.
internal sealed class HostedRegionTestPage(HostedRegionDirectory directory, DesktopWindow? nativeWindow, string embedder = "http://127.0.0.1:38200",
    Runic.Application.Views.IRunicWindow? window = null, bool browser = false) : IHostedRegionPage
{
    internal static readonly TimeSpan ReportDelay = TimeSpan.FromMilliseconds(50);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Region> _regions = new(StringComparer.Ordinal);

    // One region of the page: its report, and what the host shows in it.
    internal sealed class Region(string name, HostedRegionSizing sizing)
    {
        internal string Name { get; } = name;
        internal HostedRegionSizing Sizing { get; } = sizing;
        internal double Width { get; set; } = 800;
        internal double Height { get; set; } = 520;
        internal double Scale { get; set; } = 1.5;
        internal double X { get; set; }
        internal double Y { get; set; } = 96;
        internal bool Visible { get; set; } = true;
        internal bool Focused { get; set; }
        internal HostedRegionFocusSource? Source { get; set; }

        // The host's last push: content, component, waiting, detached or ended; null before the first.
        internal string? Shows { get; set; }
        internal string? Src { get; set; }
        internal string? Allow { get; set; }
        // A component's module and token (region-component/1); Src is then its WebSocket address.
        internal string? Module { get; set; }
        internal string? Token { get; set; }
        internal string? Title { get; set; }
        internal int? AppliedHeight { get; set; }
        internal int FocusRequests { get; set; }

        internal HostedRegionReport Report() => new(Name, "c1", Width, Height, Scale, X, Y, Visible, Focused, Focused ? Source : null);
    }

    public Runic.Application.Views.IRunicWindow? Window => window;

    public DesktopWindow? NativeWindow => nativeWindow;

    public string Embedder => embedder;

    public bool IsBrowser => browser;

    public bool AllowsSameSite => !browser;

    // Whether an element of the page outside its regions holds the focus.
    internal bool HostFocused { get; set; }

    internal Region? Find(string name)
    {
        lock (_gate) return _regions.GetValueOrDefault(name);
    }

    internal Region Get(string name) => Find(name) ?? throw new InvalidOperationException($"The page declares no region {name}.");

    // hosted-region-declare, with its first hosted-region-state.
    internal Region Declare(string name, HostedRegionSizing sizing = HostedRegionSizing.Fill)
    {
        var region = new Region(name, sizing);
        lock (_gate) _regions[name] = region;
        directory.Declare(this, name, sizing, region.Report());
        return region;
    }

    // hosted-region-remove, or the page's connection closed (reload).
    internal void Remove(string name, string reason = "removed")
    {
        lock (_gate) _regions.Remove(name);
        directory.Remove(this, name, reason);
    }

    internal void Report(string name, Action<Region> change)
    {
        var region = Get(name);
        HostedRegionReport report;
        lock (_gate)
        {
            change(region);
            report = region.Report();
        }
        directory.Report(this, report);
    }

    // The user moves the focus into the region (Tab, Shift+Tab, a click) or the page's script does.
    internal void FocusRegion(string name, HostedRegionFocusSource source)
    {
        List<string> others;
        lock (_gate)
        {
            HostFocused = false;
            others = [.. _regions.Values.Where(region => region.Focused && region.Name != name).Select(static region => region.Name)];
        }
        foreach (var other in others) Report(other, static region => region.Focused = false);
        Report(name, region =>
        {
            region.Focused = true;
            region.Source = source;
        });
    }

    // The user moves the focus to an element of the page outside its regions, or to its body (focused false).
    internal void FocusHost(bool focused = true)
    {
        List<string> blurred;
        lock (_gate)
        {
            HostFocused = focused;
            blurred = [.. _regions.Values.Where(static region => region.Focused).Select(static region => region.Name)];
        }
        foreach (var name in blurred) Report(name, static region => region.Focused = false);
    }

    // The host window closed.
    internal void Close() => directory.PageClosed(this);

    public bool ShowFrame(string name, string src, string? title, string allow) => Push(name, region =>
    {
        region.Shows = "content";
        region.Src = src;
        region.Title = title;
        region.Allow = allow;
    });

    public bool ShowComponent(string name, string module, string url, string token) => Push(name, region =>
    {
        region.Shows = "component";
        region.Src = url;
        region.Module = module;
        region.Token = token;
    });

    public bool Update(string name, HostedRegionTitle title, int? height) => Push(name, region =>
    {
        if (title.HasValue) region.Title = title.Value;
        if (height is { } value) region.AppliedHeight = value;
    });

    public bool Empty(string name, HostedRegionEmptyReason reason) => Push(name, region =>
    {
        region.Shows = reason switch
        {
            HostedRegionEmptyReason.Waiting => "waiting",
            HostedRegionEmptyReason.Detached => "detached",
            _ => "ended",
        };
        region.Src = null;
    });

    public bool Focus(string name)
    {
        bool moves;
        lock (_gate)
        {
            if (!_regions.TryGetValue(name, out var region)) return false;
            region.FocusRequests++;
            moves = !HostFocused && !region.Focused;
        }
        if (moves)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(ReportDelay);
                if (Find(name) is null) return;
                Report(name, region =>
                {
                    region.Focused = true;
                    region.Source = HostedRegionFocusSource.Script;
                });
            });
        }
        return true;
    }

    private bool Push(string name, Action<Region> push)
    {
        lock (_gate)
        {
            if (!_regions.TryGetValue(name, out var region)) return false;
            push(region);
            return true;
        }
    }
}
