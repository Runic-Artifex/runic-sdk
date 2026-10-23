using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>A native message box: a short message, optional detail and the buttons that answer it.</summary>
/// <param name="Message">The primary text, such as a question; not empty, without NUL.</param>
/// <remarks>
/// <para>
/// Show it through <see cref="IDesktopMessageBoxes.ShowAsync"/> of the window it belongs to. Use a native box where
/// the page cannot answer, such as before it loaded or while it is busy, for close confirmations that must follow the
/// platform's button order and keys, and for alerts a screen reader must announce as such; keep ordinary prompts in
/// the page.
/// </para>
/// <para>
/// Rules, checked when the box is shown: at most six <see cref="Buttons"/>, each with a non-empty unique
/// <see cref="DesktopMessageBoxButton.Id"/> and a label that is not blank, at most one
/// <see cref="DesktopMessageBoxButtonRole.Default"/> and one <see cref="DesktopMessageBoxButtonRole.Cancel"/> button,
/// and no text with NUL.
/// </para>
/// </remarks>
public sealed record DesktopMessageBox(string Message)
{
    /// <summary>Secondary text below the message, such as what happens next; <see langword="null"/> for none.</summary>
    public string? Detail { get; init; }

    /// <summary>The window title, where the box has one: the caption on Windows and the title on GTK 3.</summary>
    /// <remarks>
    /// macOS sheets and GTK 4 alert dialogs have no title and ignore it. On Windows, <see langword="null"/> takes the
    /// owner window's title.
    /// </remarks>
    public string? Title { get; init; }

    /// <summary>The icon and alert level; <see cref="DesktopMessageBoxKind.None"/> by default.</summary>
    public DesktopMessageBoxKind Kind { get; init; }

    /// <summary>The buttons, in the order of the application; empty, the default, shows one localized OK or Close button.</summary>
    /// <remarks>
    /// Each platform places the buttons by role: Windows shows the <see cref="DesktopMessageBoxButtonRole.Default"/>
    /// button first and the <see cref="DesktopMessageBoxButtonRole.Cancel"/> button last, macOS the default button
    /// rightmost with the cancel button beside it, and GTK the default button last with the cancel button before it.
    /// Other buttons keep their order.
    /// </remarks>
    public ImmutableArray<DesktopMessageBoxButton> Buttons { get; init; } = [];

    /// <summary>The required ownership; message boxes are always owned by their window.</summary>
    public OwnerPolicy OwnerPolicy { get; init; } = OwnerPolicy.RequireOwner;
}

/// <summary>The icon and alert level of a <see cref="DesktopMessageBox"/>.</summary>
/// <remarks>
/// This set is open: a minor release may add kinds, and a provider shows an unknown kind as
/// <see cref="None"/>. macOS shows the application icon for every kind and marks <see cref="Error"/> as critical; GTK 4
/// alert dialogs show no icon.
/// </remarks>
public enum DesktopMessageBoxKind
{
    /// <summary>No icon.</summary>
    None = 0,
    /// <summary>Information, such as a finished operation.</summary>
    Information = 1,
    /// <summary>A warning, such as a question about losing changes.</summary>
    Warning = 2,
    /// <summary>An error.</summary>
    Error = 3,
}

/// <summary>A button of a <see cref="DesktopMessageBox"/>.</summary>
/// <param name="Id">The application's identifier of the button; not empty and unique in its box.</param>
/// <param name="Label">The text shown, such as "Save"; not blank. <c>&amp;</c> and <c>_</c> are shown as written;
/// set <see cref="AccessKey"/> for an access key.</param>
public sealed record DesktopMessageBoxButton(string Id, string Label)
{
    /// <summary>What the button does for the platform: whether Return or Esc choose it and how it is styled.</summary>
    public DesktopMessageBoxButtonRole Role { get; init; }

    /// <summary>A letter or digit that chooses the button with Alt (Windows, GTK), as <see cref="DesktopMenuItem.AccessKey"/>.</summary>
    /// <remarks>
    /// The first letter of the label that matches it in any case is underlined; a label without it gets the key in
    /// parentheses after it, as for menu items. macOS has no access keys and ignores it.
    /// </remarks>
    public char? AccessKey { get; init; }
}

/// <summary>The role of a <see cref="DesktopMessageBoxButton"/>.</summary>
/// <remarks>This set is open: a minor release may add roles, and a provider treats an unknown role as <see cref="Normal"/>.</remarks>
public enum DesktopMessageBoxButtonRole
{
    /// <summary>An ordinary button.</summary>
    Normal = 0,
    /// <summary>The button Return chooses and the platform emphasizes; at most one per box.</summary>
    Default = 1,
    /// <summary>The button Esc and the window's close button choose; at most one per box.</summary>
    /// <remarks>Without a cancel button, Windows and macOS offer no way to close the box other than its buttons.</remarks>
    Cancel = 2,
    /// <summary>A button that discards data, such as "Don't Save": styled as destructive on macOS 11 and later and on GTK 3.</summary>
    Destructive = 3,
}

/// <summary>Native message boxes owned by one window.</summary>
/// <remarks>
/// <see cref="IWindowPlatformServices.MessageBoxes"/> of a window, which a ViewModel can also take directly. Its
/// capability is <see cref="PlatformCapabilityIds.MessageBoxes"/>. Methods are safe to call from any thread; the box is
/// shown on the window's thread.
/// </remarks>
public interface IDesktopMessageBoxes
{
    /// <summary>Shows a message box modal to the window and returns the button the user chose.</summary>
    /// <param name="messageBox">The box; see <see cref="DesktopMessageBox"/> for its rules.</param>
    /// <param name="cancellationToken">Closes the box, which then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> with the chosen button, one of <see cref="DesktopMessageBox.Buttons"/>, or
    /// with <see langword="null"/> when the box had no buttons of the application or GTK closed it without a button,
    /// such as when the user pressed Esc in a box without a cancel button. <see cref="PlatformResult{T}.Unavailable"/>:
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/> before the window opens or on a host without a native
    /// owner, <see cref="PlatformUnavailableReason.OwnerClosed"/> once it closed, also while the box is shown, and
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/> when the platform cannot show the box, such as GTK
    /// before 4.10.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="messageBox"/> breaks a rule.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="messageBox"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    ValueTask<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken = default);
}

/// <summary>Message boxes for hosts that cannot show them.</summary>
public static class DesktopMessageBoxes
{
    internal static readonly IDesktopMessageBoxes OwnerUnavailable = new UnavailableMessageBoxes(PlatformUnavailableReason.OwnerUnavailable);

    /// <summary>Creates message boxes that report every box unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>They check the box as a provider does, so a box that is wrong on one platform is wrong on every
    /// platform; nothing else throws except caller cancellation.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not defined.</exception>
    public static IDesktopMessageBoxes Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return reason == PlatformUnavailableReason.OwnerUnavailable ? OwnerUnavailable : new UnavailableMessageBoxes(reason);
    }

    private sealed class UnavailableMessageBoxes(PlatformUnavailableReason reason) : IDesktopMessageBoxes, IDesktopServiceStatus
    {
        public CapabilityStatus Status { get; } = DesktopServiceStatus.Unavailable(reason);

        public ValueTask<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken = default)
        {
            DesktopMessageBoxRules.Check(messageBox);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<DesktopMessageBoxButton?>>(new PlatformResult<DesktopMessageBoxButton?>.Unavailable(reason));
        }
    }
}
