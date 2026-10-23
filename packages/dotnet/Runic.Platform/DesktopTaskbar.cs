namespace Runic.Platform;

/// <summary>A badge on the window's taskbar button or the application's Dock icon: a count, or a dot that asks for attention.</summary>
/// <remarks>
/// Set it through <see cref="IWindowPlatformServices.SetBadgeAsync"/>. Windows draws it as an overlay on the window's
/// taskbar button, macOS as the Dock icon's badge, and Linux docks that read the Unity launcher API, such as KDE Plasma
/// and Ubuntu's dock, as the count on the launcher.
/// </remarks>
public sealed record DesktopBadge
{
    /// <summary>The count, at least 1; more than 99 shows <c>99+</c>. <see langword="null"/>, the default, shows a dot.</summary>
    public int? Count { get; init; }

    /// <summary>What the badge means, for screen readers, such as "3 unread messages"; Windows reads it with the taskbar button.</summary>
    public string? Description { get; init; }
}

/// <summary>Progress shown on the window's taskbar button or the application's Dock icon.</summary>
/// <param name="Value">How far the work is, from 0 to 1; <see langword="null"/> for work whose end is unknown.</param>
/// <remarks>Set it through <see cref="IWindowPlatformServices.SetProgressAsync"/>, and clear it with <see langword="null"/> when the work ends.</remarks>
public sealed record DesktopProgress(double? Value)
{
    /// <summary>Whether the work runs, is paused or failed; Windows colours the bar, macOS and Linux show <see cref="DesktopProgressState.Normal"/>.</summary>
    public DesktopProgressState State { get; init; }
}

/// <summary>The state of the work that a <see cref="DesktopProgress"/> shows.</summary>
/// <remarks>This set is open: a minor release may add states, so treat unknown values as <see cref="Normal"/>.</remarks>
public enum DesktopProgressState
{
    /// <summary>The work runs.</summary>
    Normal = 0,
    /// <summary>The work is paused; a yellow bar on Windows.</summary>
    Paused = 1,
    /// <summary>The work failed; a red bar on Windows.</summary>
    Error = 2,
}
