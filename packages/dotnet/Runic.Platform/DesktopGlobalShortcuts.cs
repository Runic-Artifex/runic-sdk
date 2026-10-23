using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>A named action that the user can start with a key while the application is in the background.</summary>
/// <param name="Id">
/// The action's identifier: 1 to 64 ASCII letters, digits, dots, underscores or hyphens, unique in a set. It names the
/// action in <see cref="DesktopGlobalShortcutBinding"/> and <see cref="DesktopGlobalShortcutActivation"/>; on Linux the
/// desktop keeps the user's binding under it, so keep it stable across releases.
/// </param>
/// <param name="Description">What the action does, at most 256 characters without NUL. The Linux desktop shows it where the user assigns keys.</param>
public sealed record DesktopGlobalShortcut(string Id, string Description)
{
    /// <summary>Gets the key to bind, or <see langword="null"/> to leave the action unbound.</summary>
    /// <remarks>
    /// On Windows and macOS this is the binding: the application records the user's choice, for example with
    /// <c>shortcutFromEvent</c> of <c>@runic-artifex/views</c>, keeps its <see cref="DesktopShortcut.ToString"/> text
    /// in its settings and passes it here. On Linux it is a suggestion: the desktop asks the user or applies its own
    /// assignment, and keeps the user's later changes.
    /// </remarks>
    public DesktopShortcut? PreferredShortcut { get; init; }
}

/// <summary>Whether a global shortcut is bound, as <see cref="DesktopGlobalShortcutBinding.Status"/> reports.</summary>
/// <remarks>This set is open: a minor release may add values, so handle unknown values.</remarks>
public enum DesktopGlobalShortcutStatus
{
    /// <summary>
    /// No key is assigned: the action has no preferred shortcut, or on Linux the user left it unassigned. The default
    /// value, so a binding that does not set <see cref="DesktopGlobalShortcutBinding.Status"/> reports no key.
    /// </summary>
    Unbound = 0,

    /// <summary>The key starts the action.</summary>
    Bound = 1,

    /// <summary>Another application, or another action of this one, already uses the key.</summary>
    Conflict = 2,

    /// <summary>
    /// The platform does not allow the key as a global shortcut: a key the system or the focused control keeps, such as
    /// F12 on Windows or Ctrl+C, a combination macOS rejects, or a character the current keyboard layout cannot type.
    /// </summary>
    Refused = 3,
}

/// <summary>The outcome of binding one <see cref="DesktopGlobalShortcut"/>.</summary>
/// <param name="Id">The action's <see cref="DesktopGlobalShortcut.Id"/>.</param>
public sealed record DesktopGlobalShortcutBinding(string Id)
{
    /// <summary>Gets the bound key, or <see langword="null"/> when none is bound or the desktop's key has no <see cref="DesktopShortcut"/> form.</summary>
    /// <remarks>
    /// For <see cref="DesktopGlobalShortcutStatus.Conflict"/> and <see cref="DesktopGlobalShortcutStatus.Refused"/> it is
    /// the requested key, so the application can tell the user which one failed.
    /// </remarks>
    public DesktopShortcut? Shortcut { get; init; }

    /// <summary>Gets the key as the user sees it: the Linux desktop's own text, elsewhere <see cref="DesktopShortcut.ToDisplayString()"/>; <see langword="null"/> when no key is assigned.</summary>
    public string? TriggerDescription { get; init; }

    /// <summary>Gets whether the key is bound.</summary>
    public DesktopGlobalShortcutStatus Status { get; init; }
}

/// <summary>A key press of a bound global shortcut. Treat it as untrusted input at the application boundary.</summary>
/// <param name="Id">The action's <see cref="DesktopGlobalShortcut.Id"/>.</param>
public sealed record DesktopGlobalShortcutActivation(string Id)
{
    /// <summary>Short-lived desktop activation data for raising a window. Dispatch promptly; do not persist it.</summary>
    /// <remarks>Linux passes the activation token the portal sends; Windows and macOS pass none.</remarks>
    public DesktopActivationContext? PlatformContext { get; init; }
}

/// <summary>The bindings after the user or the desktop changed them.</summary>
/// <param name="Bindings">Every action's binding, in the order of the last <see cref="IDesktopGlobalShortcuts.BindAsync"/>.</param>
public sealed record DesktopGlobalShortcutsChange(ImmutableArray<DesktopGlobalShortcutBinding> Bindings);

/// <summary>
/// The application's global shortcuts: keys that start named actions while another application has the focus. One set
/// for the application; disposing it removes every binding.
/// </summary>
/// <remarks>
/// Windows registers each key with <c>RegisterHotKey</c> and macOS with <c>RegisterEventHotKey</c>, so the application
/// chooses the keys, usually from its own settings UI. Linux uses the desktop's GlobalShortcuts portal: the desktop owns the
/// bindings and the user changes them in its settings (<see cref="ConfigureAsync"/>). Only key presses are reported, not
/// releases, and a held key reports one press.
/// </remarks>
public interface IDesktopGlobalShortcuts : IAsyncDisposable
{
    /// <summary>Raised on a worker thread for each press of a bound key; dispatch window work through its presentation.</summary>
    event EventHandler<DesktopGlobalShortcutActivation>? Activated;

    /// <summary>Raised on a worker thread after the user or the desktop changed a binding, for example in the Linux desktop's settings or after a keyboard layout change on macOS.</summary>
    event EventHandler<DesktopGlobalShortcutsChange>? BindingsChanged;

    /// <summary>Replaces the whole set of actions and binds their keys.</summary>
    /// <param name="shortcuts">At most 32 actions with unique ids; an empty set removes every binding.</param>
    /// <param name="cancellationToken">Cancels the wait; the previous set may already be gone.</param>
    /// <returns>One binding per action, in the order given; a key that cannot be bound fails alone, with its status.</returns>
    /// <exception cref="ArgumentException">The set has more than 32 actions, a repeated id, or an id or description outside its rule.</exception>
    ValueTask<PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>> BindAsync(
        ImmutableArray<DesktopGlobalShortcut> shortcuts, CancellationToken cancellationToken = default);

    /// <summary>Gets the current bindings, with the user's changes on Linux; empty before the first <see cref="BindAsync"/>.</summary>
    ValueTask<PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the desktop's settings for these shortcuts, where the user assigns the keys.</summary>
    /// <remarks>
    /// Linux desktops with version 2 of the GlobalShortcuts portal open their dialog. Windows and macOS have no such
    /// settings and report <see cref="PlatformUnavailableReason.BackendUnavailable"/>: the application offers its own.
    /// </remarks>
    ValueTask<PlatformResult<PlatformUnit>> ConfigureAsync(CancellationToken cancellationToken = default);
}

/// <summary>Global shortcuts for hosts and platforms that cannot provide them.</summary>
public static class DesktopGlobalShortcuts
{
    /// <summary>Creates global shortcuts that report every request unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>
    /// The events are never raised. Nothing throws except for an invalid argument, caller cancellation and use after
    /// disposal.
    /// </remarks>
    public static IDesktopGlobalShortcuts Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableGlobalShortcuts(reason);
    }

    private sealed class UnavailableGlobalShortcuts(PlatformUnavailableReason reason) : IDesktopGlobalShortcuts, IDesktopServiceStatus
    {
        public CapabilityStatus Status { get; } = DesktopServiceStatus.Unavailable(reason);
        private volatile bool _disposed;

        public event EventHandler<DesktopGlobalShortcutActivation>? Activated { add { } remove { } }
        public event EventHandler<DesktopGlobalShortcutsChange>? BindingsChanged { add { } remove { } }

        public ValueTask<PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>> BindAsync(
            ImmutableArray<DesktopGlobalShortcut> shortcuts, CancellationToken cancellationToken = default)
        {
            DesktopGlobalShortcutRules.Validate(shortcuts);
            return Answer<ImmutableArray<DesktopGlobalShortcutBinding>>(cancellationToken);
        }

        public ValueTask<PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>> ListAsync(CancellationToken cancellationToken = default) =>
            Answer<ImmutableArray<DesktopGlobalShortcutBinding>>(cancellationToken);

        public ValueTask<PlatformResult<PlatformUnit>> ConfigureAsync(CancellationToken cancellationToken = default) =>
            Answer<PlatformUnit>(cancellationToken);

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }

        private ValueTask<PlatformResult<T>> Answer<T>(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<T>>(new PlatformResult<T>.Unavailable(reason));
        }
    }
}
