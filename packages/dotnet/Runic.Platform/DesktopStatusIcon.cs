using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>How much attention a status icon asks for.</summary>
/// <remarks>This set is closed: it gains values only in a major release.</remarks>
public enum DesktopStatusIconStatus
{
    /// <summary>Not important now; the desktop may move the icon into an overflow area.</summary>
    Passive,
    /// <summary>Shown normally.</summary>
    Active,
    /// <summary>Asks for attention, with the attention icon where one is supplied.</summary>
    NeedsAttention
}

/// <summary>Straight-alpha RGBA pixels in rows from the top, four bytes per pixel.</summary>
public sealed record DesktopIconImage(int Width, int Height, ImmutableArray<byte> Rgba);

/// <summary>An icon from the desktop's icon theme, with images for desktops that lack it.</summary>
public sealed record DesktopIcon
{
    /// <summary>A freedesktop icon name such as <c>system-software-update</c>; used where the desktop has an icon theme.</summary>
    public string? IconName { get; init; }
    /// <summary>Images in one or more sizes; the desktop picks the closest.</summary>
    public ImmutableArray<DesktopIconImage> Images { get; init; } = [];
}

/// <summary>An entry in a status icon's menu.</summary>
/// <remarks>This set is closed: it gains cases only in a major release.</remarks>
public abstract record DesktopMenuItem
{
    private protected DesktopMenuItem() { }
}

/// <summary>A menu command. Its ID is an application-owned routing key, never a command.</summary>
public sealed record DesktopMenuCommand(string Id, string Label) : DesktopMenuItem
{
    /// <summary>Whether the command can be chosen.</summary>
    public bool IsEnabled { get; init; } = true;
    /// <summary>Null for a plain command; true or false for a check item and its state.</summary>
    public bool? IsChecked { get; init; }
}

/// <summary>A line between groups of menu entries.</summary>
public sealed record DesktopMenuSeparator : DesktopMenuItem;

/// <summary>A labelled submenu.</summary>
public sealed record DesktopMenuSubmenu(string Label, ImmutableArray<DesktopMenuItem> Items) : DesktopMenuItem
{
    /// <summary>Whether the submenu can be opened.</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>Everything a status icon shows. Each <see cref="IDesktopStatusIcon.ShowAsync"/> replaces the whole state.</summary>
public sealed record DesktopStatusIconState(DesktopIcon Icon, string Title)
{
    /// <summary>How much attention the icon asks for.</summary>
    public DesktopStatusIconStatus Status { get; init; } = DesktopStatusIconStatus.Active;
    /// <summary>The icon shown while <see cref="Status"/> is <see cref="DesktopStatusIconStatus.NeedsAttention"/>; the normal icon otherwise.</summary>
    public DesktopIcon? AttentionIcon { get; init; }
    /// <summary>Text shown below <see cref="Title"/> in the tooltip.</summary>
    public string? Tooltip { get; init; }
    /// <summary>The menu, opened with the secondary button. An empty menu shows none.</summary>
    public ImmutableArray<DesktopMenuItem> Menu { get; init; } = [];
}

/// <summary>How the user activated a status icon.</summary>
/// <remarks>This set is open: a minor release may add kinds, so handle unknown values.</remarks>
public enum DesktopStatusIconActivationKind
{
    /// <summary>The primary action, usually a left click.</summary>
    Primary,
    /// <summary>The secondary action, usually a middle click.</summary>
    Secondary,
    /// <summary>A menu command; see <see cref="DesktopStatusIconActivation.MenuItemId"/>.</summary>
    MenuItem
}

/// <summary>A user action on a status icon. Treat activation as untrusted input at the application boundary.</summary>
public sealed record DesktopStatusIconActivation(DesktopStatusIconActivationKind Kind, string? MenuItemId = null)
{
    /// <summary>Short-lived desktop activation data for raising a window. Dispatch promptly; do not persist it.</summary>
    public DesktopActivationContext? PlatformContext { get; init; }
}

/// <summary>An application's icon in the desktop's status area, with or without an open window.</summary>
public interface IDesktopStatusIcon : IAsyncDisposable
{
    /// <summary>Raised on a worker thread; dispatch window work through its presentation.</summary>
    event EventHandler<DesktopStatusIconActivation>? Activated;
    /// <summary>Shows the icon or replaces what it shows.</summary>
    /// <remarks>
    /// Success means a desktop status area accepted the icon. Without one, for example on GNOME without an
    /// AppIndicator extension, the result is <see cref="PlatformResult{T}.Unavailable"/>: the icon stays ready and
    /// appears when a status area starts, and again after the status area restarts, until it is hidden.
    /// </remarks>
    ValueTask<PlatformResult<PlatformUnit>> ShowAsync(DesktopStatusIconState state, CancellationToken cancellationToken = default);
    /// <summary>Removes the icon until the next <see cref="ShowAsync"/>.</summary>
    ValueTask<PlatformResult<PlatformUnit>> HideAsync(CancellationToken cancellationToken = default);
}

/// <summary>Status icons for hosts and platforms that cannot provide them.</summary>
public static class DesktopStatusIcon
{
    /// <summary>Creates a status icon that reports every request unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>
    /// <see cref="IDesktopStatusIcon.Activated"/> is never raised. Nothing throws except for a missing argument,
    /// caller cancellation and use after disposal.
    /// </remarks>
    public static IDesktopStatusIcon Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableStatusIcon(reason);
    }

    private sealed class UnavailableStatusIcon(PlatformUnavailableReason reason) : IDesktopStatusIcon
    {
        private readonly PlatformResult<PlatformUnit> _result = new PlatformResult<PlatformUnit>.Unavailable(reason);
        private volatile bool _disposed;

        public event EventHandler<DesktopStatusIconActivation>? Activated { add { } remove { } }

        public ValueTask<PlatformResult<PlatformUnit>> ShowAsync(DesktopStatusIconState state, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(state);
            return Answer(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> HideAsync(CancellationToken cancellationToken = default) => Answer(cancellationToken);

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }

        private ValueTask<PlatformResult<PlatformUnit>> Answer(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_result);
        }
    }
}
