namespace Runic.Platform;

/// <summary>The direction native window parts and menus lay out in.</summary>
/// <remarks>
/// A closed set. <see cref="Default"/> keeps the platform's behaviour of earlier releases, so an application opts in to
/// mirroring. A page sets its own direction with <c>&lt;html dir="rtl"&gt;</c>.
/// </remarks>
public enum DesktopLayoutDirection
{
    /// <summary>The platform's default: what the window or menu did before the option existed.</summary>
    Default = 0,

    /// <summary>Left to right.</summary>
    LeftToRight = 1,

    /// <summary>Right to left, as for Arabic, Hebrew or Persian: the frame, its buttons and menus are mirrored.</summary>
    RightToLeft = 2,
}
