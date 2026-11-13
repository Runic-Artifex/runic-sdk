using Runic.Desktop;

namespace Runic.Application.Views.Desktop.Hosting;

// One host window's page as region mode sees it (host-protocol.md, Regions; specs/application/README.md, Hosted
// regions): the regions it declares, which reach the directory, and the pushes that tell it what each shows. Runic's
// pages talk hosted-regions/1 through the window's Views connection (DesktopHostedRegionPage); tests play a page.
internal interface IHostedRegionPage
{
    // The host's Window whose page this is, or null for a page of no Runic Window, as in tests.
    IRunicWindow? Window { get; }

    // The host's native window that shows the page, which owns its regions' dialogs and context menus; null for a page in
    // the user's browser that a browser (serve-only) host serves.
    DesktopWindow? NativeWindow { get; }

    // The page's origin, which frames the guest's page (region-attach embedder), unless a declaration names its own.
    string Embedder { get; }

    // Whether the page runs in an installed browser rather than the host's own web view (region-attach browser): a browser
    // (serve-only) host's pages, and a native host's browser fallback. An elevated guest refuses such a page.
    bool IsBrowser { get; }

    // Whether the guest may serve the frame from the embedder's own loopback family (region-attach allowSameSite): an
    // embedded host allows it, a browser host never does.
    bool AllowsSameSite { get; }

    bool ShowFrame(string name, string src, string? title, string allow);

    // region-component/1: shows the guest's component in the region: its module, its WebSocket address and its token.
    bool ShowComponent(string name, string module, string url, string token);

    bool Update(string name, Runic.Application.Views.HostedRegionTitle title, int? height);

    bool Empty(string name, Runic.Application.Views.HostedRegionEmptyReason reason);

    bool Focus(string name);
}

// A region that a page declares now.
internal sealed class HostedRegionSlot(IHostedRegionPage page, string name, Runic.Application.Views.HostedRegionSizing sizing, string? embedder = null)
{
    internal IHostedRegionPage Page { get; } = page;
    internal string Name { get; } = name;
    internal Runic.Application.Views.HostedRegionSizing Sizing { get; } = sizing;

    // The origin of the page that declared the region, which frames the guest's page (region-attach embedder).
    internal string Embedder { get; } = embedder ?? page.Embedder;

    // The page's latest report; null before its first.
    internal Runic.Application.Views.HostedRegionReport? Report { get; set; }

    // The region window the slot shows, or null; read and changed under the directory's gate.
    internal RunicHostedRegion? Region { get; set; }
}

// The regions the host's pages declare and the region windows of its guests, which the directory brings together
// (host-protocol.md, Lifecycle in a region): a region window takes the first region of its name in its host window, or
// in any host window for a name-only decision, and comes back to the same host window's region of that name when the
// page declares it again.
internal sealed class HostedRegionDirectory
{
    private readonly object _gate = new();
    private readonly Dictionary<IHostedRegionPage, Dictionary<string, HostedRegionSlot>> _pages = new(ReferenceEqualityComparer.Instance);
    // The open region windows, in the order they opened.
    private readonly List<RunicHostedRegion> _regions = [];

    internal object Gate => _gate;

    // ----- The pages' side

    internal void Declare(IHostedRegionPage page, string name, Runic.Application.Views.HostedRegionSizing sizing,
        Runic.Application.Views.HostedRegionReport? report = null, string? embedder = null)
    {
        HostedRegionSlot slot;
        HostedRegionSlot? replaced;
        RunicHostedRegion? replacedRegion = null;
        RunicHostedRegion? match;
        lock (_gate)
        {
            if (!_pages.TryGetValue(page, out var slots)) _pages[page] = slots = new(StringComparer.Ordinal);
            // A second declaration of the name in the page, which the producer reports as a removal first; kept robust.
            replaced = slots.GetValueOrDefault(name);
            slot = new HostedRegionSlot(page, name, sizing, embedder) { Report = report };
            slots[name] = slot;
            if (replaced?.Region is { } shown)
            {
                replacedRegion = shown;
                shown.UnbindLocked(replaced);
            }
            match = FindRegionLocked(slot);
            match?.BindLocked(slot);
        }
        if (replaced is not null) replacedRegion?.OnSlotRemoved(replaced, "reload");
        if (match is not null) match.OnBound(slot);
        else page.Empty(name, Runic.Application.Views.HostedRegionEmptyReason.Waiting);
    }

    // The page removed the region, or its connection closed (reason reload).
    internal void Remove(IHostedRegionPage page, string name, string reason)
    {
        HostedRegionSlot? slot;
        RunicHostedRegion? region = null;
        lock (_gate)
        {
            if (!_pages.TryGetValue(page, out var slots) || !slots.Remove(name, out slot)) return;
            if (slot.Region is { } shown)
            {
                region = shown;
                shown.UnbindLocked(slot);
            }
        }
        region?.OnSlotRemoved(slot, reason);
    }

    internal void Report(IHostedRegionPage page, Runic.Application.Views.HostedRegionReport report)
    {
        HostedRegionSlot? slot;
        lock (_gate)
        {
            if (!_pages.TryGetValue(page, out var slots) || !slots.TryGetValue(report.Name, out slot)) return;
            slot.Report = report;
        }
        slot.Region?.OnReport(slot, report);
    }

    // The host window closed: its regions are gone, and the region windows it showed close (host-protocol.md, Lifecycle
    // in a region), since no region of the same host window can come back.
    internal void PageClosed(IHostedRegionPage page)
    {
        List<(HostedRegionSlot Slot, RunicHostedRegion Region)> removed = [];
        List<RunicHostedRegion> bound;
        lock (_gate)
        {
            if (_pages.Remove(page, out var slots))
            {
                foreach (var slot in slots.Values)
                {
                    if (slot.Region is not { } region) continue;
                    region.UnbindLocked(slot);
                    removed.Add((slot, region));
                }
            }
            bound = [.. _regions.Where(region => ReferenceEquals(region.PageLocked, page))];
        }
        foreach (var (slot, region) in removed) region.OnSlotRemoved(slot, "removed");
        foreach (var region in bound) region.OnHostWindowClosed();
    }

    // The embedder of the region a page declares now, or null (tests).
    internal string? EmbedderOf(IHostedRegionPage page, string name)
    {
        lock (_gate) return _pages.TryGetValue(page, out var slots) && slots.TryGetValue(name, out var slot) ? slot.Embedder : null;
    }

    // ----- The region windows' side

    // A region window opens: null when it may, else the reason it may not (region-ambiguous). The slot it takes now, if
    // any, is bound; the caller attaches it once the guest has the window-open reply.
    internal string? Open(RunicHostedRegion region, out HostedRegionSlot? slot)
    {
        slot = null;
        lock (_gate)
        {
            if (region.HostWindowTarget is null &&
                _pages.Where(page => page.Value.ContainsKey(region.Name)).Take(2).Count() > 1)
                return "region-ambiguous";
            _regions.Add(region);
            slot = FindSlotLocked(region);
            if (slot is not null) region.BindLocked(slot);
        }
        return null;
    }

    // The region window closed: it leaves the directory, and the region it showed may show another waiting window.
    internal void Close(RunicHostedRegion region)
    {
        HostedRegionSlot? freed;
        RunicHostedRegion? next = null;
        lock (_gate)
        {
            _regions.Remove(region);
            freed = region.SlotLocked;
            if (freed is not null)
            {
                region.UnbindLocked(freed);
                next = FindRegionLocked(freed);
                next?.BindLocked(freed);
            }
        }
        if (freed is null) return;
        if (next is not null) next.OnBound(freed);
        else freed.Page.Empty(freed.Name, Runic.Application.Views.HostedRegionEmptyReason.Ended);
    }

    // The host application detaches the window from its region: the region may show another waiting window, else it is
    // emptied as waiting. Returns the slot it left.
    internal HostedRegionSlot? Detach(RunicHostedRegion region)
    {
        HostedRegionSlot? freed;
        RunicHostedRegion? next = null;
        lock (_gate)
        {
            freed = region.SlotLocked;
            region.HostDetachedLocked = true;
            if (freed is null) return null;
            region.UnbindLocked(freed);
            next = FindRegionLocked(freed);
            next?.BindLocked(freed);
        }
        if (next is not null) next.OnBound(freed);
        else freed.Page.Empty(freed.Name, Runic.Application.Views.HostedRegionEmptyReason.Waiting);
        return freed;
    }

    // The host application moves a detached window to another region: it takes it now, if a page declares it and
    // nothing else shows there.
    internal HostedRegionSlot? Retarget(RunicHostedRegion region, IRunicWindow window, string name)
    {
        lock (_gate)
        {
            region.RetargetLocked(window, name);
            var slot = FindSlotLocked(region);
            if (slot is not null) region.BindLocked(slot);
            return slot;
        }
    }

    // A slot of the region's name in its host window, or for a name-only window in any page, that shows nothing.
    private HostedRegionSlot? FindSlotLocked(RunicHostedRegion region)
    {
        if (region.HostDetachedLocked) return null;
        foreach (var (page, slots) in _pages)
        {
            if (!slots.TryGetValue(region.Name, out var slot) || slot.Region is not null) continue;
            if (region.Accepts(page)) return slot;
        }
        return null;
    }

    // The window that a newly declared or freed slot shows: one that showed this host window's region of the name
    // before, else the oldest that waits for its first region of the name.
    private RunicHostedRegion? FindRegionLocked(HostedRegionSlot slot)
    {
        RunicHostedRegion? waiting = null;
        foreach (var region in _regions)
        {
            if (region.SlotLocked is not null || region.HostDetachedLocked || region.Name != slot.Name || !region.Accepts(slot.Page)) continue;
            if (region.PageLocked is not null) return region;
            waiting ??= region;
        }
        return waiting;
    }
}
