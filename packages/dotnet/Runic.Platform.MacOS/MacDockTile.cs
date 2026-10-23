using System.Runtime.InteropServices;
using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// Badge and progress on the application's Dock tile, which every window shares: the most recent call wins, and a
// window's badge or progress goes when its presentation stops. The badge is the tile's badgeLabel (the count, or a dot);
// progress replaces the tile's content with the application icon and an NSProgressIndicator bar, drawn again after each
// change. AppKit runs on the main thread, which the window's owner dispatches to.
internal sealed partial class MacDockTile(INativeWindowOwner owner) : IWindowTaskbar
{
    private const string Dot = "•";
    private const nint ImageScaleProportionallyUpOrDown = 3, ProgressIndicatorStyleBar = 0;
    // Main thread only.
    private static MacDockTile? s_badge, s_progress;
    private static nint s_view, s_bar;

    public ValueTask<PlatformResult<PlatformUnit>> SetBadgeAsync(DesktopBadge? badge, CancellationToken cancellationToken) =>
        InvokeAsync(() =>
        {
            ApplyBadge(badge);
            s_badge = badge is null ? null : this;
        }, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetProgressAsync(DesktopProgress? progress, CancellationToken cancellationToken) =>
        InvokeAsync(() =>
        {
            ApplyProgress(progress);
            s_progress = progress is null ? null : this;
        }, cancellationToken);

    // The window's presentation stopped: remove what it shows. The window may be gone, so this goes to the main queue.
    public ValueTask DisposeAsync() => MacOsMainQueue.InvokeAsync(() =>
    {
        if (ReferenceEquals(s_badge, this)) { ApplyBadge(null); s_badge = null; }
        if (ReferenceEquals(s_progress, this)) { ApplyProgress(null); s_progress = null; }
    });

    // The tile's badge label, for tests; main thread only.
    internal static string? BadgeLabel()
    {
        using var pool = new Pool();
        var label = Send(DockTile(), Sel("badgeLabel"));
        return label == 0 ? null : Text(label);
    }

    // Whether the tile shows the progress bar, and its value; main thread only.
    internal static (bool Shown, bool Indeterminate, double Value) Progress()
    {
        using var pool = new Pool();
        var view = Send(DockTile(), Sel("contentView"));
        return view == 0 || view != s_view || s_bar == 0 ? (false, false, 0)
            : (true, Bool(s_bar, Sel("isIndeterminate")) != 0, Double(s_bar, Sel("doubleValue")));
    }

    private async ValueTask<PlatformResult<PlatformUnit>> InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsAvailable) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        var generation = owner.Generation;
        PlatformResult<PlatformUnit> result = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        try
        {
            await owner.InvokeAsync(_ =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!owner.IsAvailable || owner.Generation != generation) return;
                using var pool = new Pool();
                action();
                result = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
            }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OwnerClosedException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (ObjectDisposedException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
    }

    private static nint DockTile() => Send(MacAppKit.EnsureApplication(), Sel("dockTile"));

    private static void ApplyBadge(DesktopBadge? badge)
    {
        using var pool = new Pool();
        string? label = badge is null ? null : DesktopTaskbarRules.Label(badge) is { Length: > 0 } count ? count : Dot;
        Arg(DockTile(), Sel("setBadgeLabel:"), label is null ? 0 : String(label));
    }

    private static void ApplyProgress(DesktopProgress? progress)
    {
        using var pool = new Pool();
        var tile = DockTile();
        if (progress is null)
        {
            if (s_view != 0)
            {
                Arg(tile, Sel("setContentView:"), 0);
                Release(s_bar);
                Release(s_view);
                s_view = s_bar = 0;
            }
            Send(tile, Sel("display"));
            return;
        }
        if (s_view == 0)
        {
            var size = SendSize(tile, Sel("size"));
            s_view = ArgRect(Send(Class("NSImageView"), Sel("alloc")), Sel("initWithFrame:"), new(0, 0, size.Width, size.Height));
            Arg(s_view, Sel("setImage:"), Send(MacAppKit.EnsureApplication(), Sel("applicationIconImage")));
            Arg(s_view, Sel("setImageScaling:"), ImageScaleProportionallyUpOrDown);
            s_bar = ArgRect(Send(Class("NSProgressIndicator"), Sel("alloc")), Sel("initWithFrame:"),
                new(size.Width * 0.08, size.Height * 0.04, size.Width * 0.84, size.Height * 0.16));
            Arg(s_bar, Sel("setStyle:"), ProgressIndicatorStyleBar);
            ArgDouble(s_bar, Sel("setMinValue:"), 0);
            ArgDouble(s_bar, Sel("setMaxValue:"), 1);
            Arg(s_view, Sel("addSubview:"), s_bar);
        }
        Arg(s_bar, Sel("setIndeterminate:"), progress.Value is null ? 1 : 0);
        if (progress.Value is { } value) ArgDouble(s_bar, Sel("setDoubleValue:"), value);
        if (Send(tile, Sel("contentView")) != s_view) Arg(tile, Sel("setContentView:"), s_view);
        Send(tile, Sel("display"));
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Rect(double X, double Y, double Width, double Height);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint ArgRect(nint target, nint selector, Rect value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial Size SendSize(nint target, nint selector);
}
