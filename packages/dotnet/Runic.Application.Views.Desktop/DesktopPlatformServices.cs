using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Runic.Desktop;
using Runic.Platform;
#if RUNIC_PORTABLE_PLATFORMS
using Runic.Platform.Linux;
using Runic.Platform.Linux.Gtk4;
using Runic.Platform.Linux.Portal;
using Runic.Platform.MacOS;
#endif
using Runic.Platform.Runtime;
using Runic.Platform.Windows;

namespace Runic.Application.Views.Desktop;

/// <summary>The native provider that <see cref="PlatformServices"/> selects for a Desktop window.</summary>
public enum DesktopPlatformProvider
{
    /// <summary>No provider: the window has no native owner, or the process runs no toolkit Runic recognizes.</summary>
    None,
    /// <summary>Win32 dialogs, clipboard and shell handoff from <c>Runic.Platform.Windows</c>.</summary>
    Windows,
    /// <summary>AppKit dialogs, clipboard and file handoff from <c>Runic.Platform.MacOS</c>.</summary>
    MacOS,
    /// <summary>xdg-desktop-portal dialogs and handoff parented through GTK 3, and the GTK 3 clipboard, from <c>Runic.Platform.Linux</c>.</summary>
    LinuxGtk3,
    /// <summary>xdg-desktop-portal dialogs and handoff parented through GTK 4, and the GTK 4 clipboard, from <c>Runic.Platform.Linux.Gtk4</c>.</summary>
    LinuxGtk4,
}

/// <summary>Selects the native platform-service provider for the running Desktop backend.</summary>
/// <remarks>
/// Applications that open windows with <c>OpenWindowAsync</c> on <see cref="RunicDesktopHost"/> call
/// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices"/> instead and take
/// <see cref="IWindowPlatformServices"/> or <see cref="IFileDialogs"/> in a ViewModel constructor. The explicit
/// providers, such as <c>WindowsPlatformProvider</c>, remain available for custom composition.
/// </remarks>
public static class PlatformServices
{
    /// <summary>Gets the provider for the operating system and the Linux toolkit this process has started.</summary>
    /// <remarks>
    /// On Linux the toolkit is the one the embedded Desktop window host claimed for the process, so a GTK 4 process
    /// never selects the GTK 3 provider. Before any embedded window starts a toolkit, Linux reports
    /// <see cref="DesktopPlatformProvider.None"/>.
    /// </remarks>
    public static DesktopPlatformProvider CurrentProvider => Select(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(),
        OperatingSystem.IsLinux(),
        gtk3Claimed: !LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk4WebKit6),
        gtk4Claimed: !LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk3WebKit41));

    // A process claims at most one Linux toolkit; a claim makes the other one unusable.
    internal static DesktopPlatformProvider Select(bool windows, bool macOS, bool linux, bool gtk3Claimed, bool gtk4Claimed) =>
        windows ? DesktopPlatformProvider.Windows
        : macOS ? DesktopPlatformProvider.MacOS
        : !linux ? DesktopPlatformProvider.None
        : gtk4Claimed ? DesktopPlatformProvider.LinuxGtk4
        : gtk3Claimed ? DesktopPlatformProvider.LinuxGtk3
        : DesktopPlatformProvider.None;

    /// <summary>Creates platform services for an open Desktop window, using <see cref="CurrentProvider"/>.</summary>
    /// <param name="owner">The window's native owner.</param>
    /// <returns>Services the caller disposes before the window's event loop stops; disposal releases acquired file access.</returns>
    /// <remarks>
    /// A window without native dispatch, such as an installed browser after an embedded-window fallback, gets services
    /// that report <see cref="PlatformUnavailableReason.OwnerUnavailable"/>; a native window whose toolkit has no
    /// provider reports <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// </remarks>
    public static DesktopWindowPlatformServices ForWindow(DesktopNativeOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var services = new DesktopWindowPlatformServices();
        services.Bind(owner);
        return services;
    }

    /// <summary>Creates the desktop's appearance preferences from the provider for the operating system.</summary>
    /// <returns>Application-scoped preferences that the caller disposes at shutdown.</returns>
    /// <remarks>
    /// Windows reads its settings, macOS reads AppKit once the Desktop event loop runs, and Linux reads
    /// xdg-desktop-portal with either toolkit, without loading GTK. Elsewhere, or where the Windows provider does not
    /// support the running version, every read reports <see cref="PlatformUnavailableReason.ProviderNotConfigured"/>.
    /// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices"/> registers one instance for
    /// the application.
    /// </remarks>
    public static IDesktopSettings CreateSettings()
    {
        IDesktopSettings? settings = null;
        if (OperatingSystem.IsWindows()) settings = Optional(CreateWindowsSettings);
#if RUNIC_PORTABLE_PLATFORMS
        else if (OperatingSystem.IsMacOS()) settings = CreateMacOSSettings();
        else if (OperatingSystem.IsLinux()) settings = CreatePortalSettings();
#endif
        return settings ?? DesktopSettings.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopSettings CreateWindowsSettings() => WindowsPlatformProvider.CreateSettings();

#if RUNIC_PORTABLE_PLATFORMS
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopSettings CreateMacOSSettings() => MacOSPlatformProvider.CreateSettings();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDesktopSettings CreatePortalSettings() => PortalPlatformProvider.CreateSettings();
#endif

    internal static IWindowPlatformServices Create(DesktopNativeOwner owner, out DesktopPlatformProvider provider)
    {
        provider = DesktopPlatformProvider.None;
        if (!owner.IsAvailable) return WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        provider = CurrentProvider;
        // Each provider is created in its own method, so the JIT loads only the selected provider's assembly:
        // a GTK 4 process does not even load the GTK 3 provider. The -windows assembly references only the
        // Windows provider, so elsewhere its providers report ProviderNotConfigured.
        NativeServices? native = null;
        if (provider == DesktopPlatformProvider.Windows) native = CreateWindows(owner);
#if RUNIC_PORTABLE_PLATFORMS
        else if (provider == DesktopPlatformProvider.MacOS && OperatingSystem.IsMacOS()) native = CreateMacOS(owner);
        else if (provider == DesktopPlatformProvider.LinuxGtk3) native = CreateGtk3(owner);
        else if (provider == DesktopPlatformProvider.LinuxGtk4 && OperatingSystem.IsLinux()) native = CreateGtk4(owner);
#endif
        if (native is not { } selected) return WindowPlatformServices.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        return new BoundWindowPlatformServices(owner, selected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeServices CreateWindows(DesktopNativeOwner owner) => new(
        WindowsPlatformProvider.CreateFileDialogs(owner),
        WindowsPlatformProvider.CreateTextClipboard(owner),
        Optional(() => WindowsPlatformProvider.CreateFileLauncher(owner)));

#if RUNIC_PORTABLE_PLATFORMS
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("macos")]
    private static NativeServices CreateMacOS(DesktopNativeOwner owner) => new(
        MacOSPlatformProvider.CreateFileDialogs(owner),
        MacOSPlatformProvider.CreateTextClipboard(owner),
        MacOSPlatformProvider.CreateFileLauncher(owner));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeServices CreateGtk3(DesktopNativeOwner owner) => new(
        LinuxPlatformProvider.CreateFileDialogs(owner),
        LinuxPlatformProvider.CreateTextClipboard(owner),
        LinuxPlatformProvider.CreateFileLauncher(owner));

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("linux")]
    private static NativeServices CreateGtk4(DesktopNativeOwner owner)
    {
        var portalOwner = Gtk4PlatformProvider.CreatePortalWindowOwner(owner);
        return new(PortalPlatformProvider.CreateFileDialogs(portalOwner), Gtk4PlatformProvider.CreateTextClipboard(owner),
            PortalPlatformProvider.CreateFileLauncher(portalOwner));
    }
#endif

    // A provider that requires a newer OS version than the one running reports the service unconfigured.
    private static T? Optional<T>(Func<T> create) where T : class
    {
        try { return create(); }
        catch (PlatformNotSupportedException) { return null; }
    }

    internal readonly record struct NativeServices(IPickerBackend Dialogs, ITextClipboard? Clipboard, IDesktopFileLauncher? Launcher);

    private sealed class BoundWindowPlatformServices : IWindowPlatformServices, IAsyncDisposable
    {
        private readonly PresentationLifetime _lifetime;
        private readonly PresentationFiles _files;
        private readonly PresentationClipboard _clipboard;
        private readonly PresentationFileLauncher _launcher;

        public BoundWindowPlatformServices(DesktopNativeOwner owner, NativeServices native)
        {
            _lifetime = new PresentationLifetime(() => owner.IsAvailable, owner.Generation);
            _files = new PresentationFiles(_lifetime, native.Dialogs);
            _clipboard = new PresentationClipboard(_lifetime, native.Clipboard);
            _launcher = new PresentationFileLauncher(_lifetime, native.Launcher);
        }

        public IFileDialogs FileDialogs => _files;
        public ITextClipboard Clipboard => _clipboard;
        public IDesktopFileLauncher FileLauncher => _launcher;

        public CapabilitySnapshot GetSnapshot() => new(_lifetime.Generation,
            _files.GetSnapshot().Statuses.SetItems(_clipboard.GetSnapshot().Statuses).SetItems(_launcher.GetSnapshot().Statuses));

        public async ValueTask DisposeAsync()
        {
            // Drains platform operations, then releases file access and provider resources. A provider resource that
            // must be released on the window's native thread cannot be once the window has gone; that is not a
            // failure of closing the window.
            try { await _lifetime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (IsOwnerClosed(error)) { }
        }

        private static bool IsOwnerClosed(Exception error) => error switch
        {
            OwnerClosedException => true,
            AggregateException aggregate => aggregate.InnerExceptions.All(IsOwnerClosed),
            _ => false,
        };
    }
}

/// <summary>The platform services of one Desktop window, bound to its native owner once the window opens.</summary>
/// <remarks>
/// <see cref="DesktopPlatformServiceCollectionExtensions.AddRunicPlatformServices"/> registers one instance per window
/// scope. Before the window opens every service reports <see cref="PlatformUnavailableReason.OwnerUnavailable"/>;
/// <c>OpenWindowAsync</c> on <see cref="RunicDesktopHost"/> binds it to the window as soon as the presentation opens, and closing the window
/// stops it. After that every service reports <see cref="PlatformUnavailableReason.OwnerClosed"/>.
/// </remarks>
public sealed class DesktopWindowPlatformServices : IWindowPlatformServices, IFileDialogs, ITextClipboard, IDesktopFileLauncher, IAsyncDisposable
{
    private static readonly IWindowPlatformServices NotOpen = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
    private static readonly IWindowPlatformServices Closed = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerClosed);
    private readonly object _gate = new();
    private IWindowPlatformServices _current = NotOpen;
    private IWindowPlatformServices? _bound;
    private Task? _disposal;
    private DesktopPlatformProvider _provider;

    internal DesktopWindowPlatformServices() { }

    /// <summary>Gets the provider selected when the window opened, or <see cref="DesktopPlatformProvider.None"/> before that.</summary>
    public DesktopPlatformProvider Provider { get { lock (_gate) return _provider; } }

    /// <summary>Gets whether the window has opened and the services are bound to it, including after it closes.</summary>
    public bool IsBound { get { lock (_gate) return _bound is not null; } }

    /// <inheritdoc />
    public IFileDialogs FileDialogs => this;
    /// <inheritdoc />
    public ITextClipboard Clipboard => this;
    /// <inheritdoc />
    public IDesktopFileLauncher FileLauncher => this;

    private IWindowPlatformServices Current => Volatile.Read(ref _current);

    internal void Bind(DesktopNativeOwner owner)
    {
        lock (_gate)
        {
            if (_bound is not null) throw new InvalidOperationException("The platform services are already bound to a window.");
            if (_disposal is not null) return;
            _bound = PlatformServices.Create(owner, out _provider);
            Volatile.Write(ref _current, _bound);
        }
    }

    /// <inheritdoc />
    public CapabilitySnapshot GetSnapshot() => Current.GetSnapshot();

    /// <summary>Stops the services: waits for platform operations, then releases file access the window still owns.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is null)
            {
                var bound = _bound;
                Volatile.Write(ref _current, Closed);
                _disposal = bound is IAsyncDisposable disposable ? disposable.DisposeAsync().AsTask() : Task.CompletedTask;
            }
            return new(_disposal);
        }
    }

    ValueTask<PickerResult<IDirectoryLease>> IFileDialogs.OpenDirectoryAsync(OpenDirectoryOptions options, CancellationToken cancellationToken) =>
        Current.FileDialogs.OpenDirectoryAsync(options, cancellationToken);
    ValueTask<PickerResult<IReadFileLease>> IFileDialogs.OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken) =>
        Current.FileDialogs.OpenFileAsync(options, cancellationToken);
    ValueTask<PickerResult<ISaveFileLease>> IFileDialogs.SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken) =>
        Current.FileDialogs.SaveFileAsync(options, cancellationToken);
    ValueTask<PlatformResult<string?>> ITextClipboard.ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken) =>
        Current.Clipboard.ReadTextAsync(maximumCharacters, cancellationToken);
    ValueTask<PlatformResult<PlatformUnit>> ITextClipboard.WriteTextAsync(string text, CancellationToken cancellationToken) =>
        Current.Clipboard.WriteTextAsync(text, cancellationToken);
    ValueTask<PlatformResult<PlatformUnit>> IDesktopFileLauncher.LaunchAsync(string path, DesktopFileOperation operation, CancellationToken cancellationToken) =>
        Current.FileLauncher.LaunchAsync(path, operation, cancellationToken);
}

/// <summary>Registers Desktop platform services for dependency injection.</summary>
public static class DesktopPlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers one <see cref="DesktopWindowPlatformServices"/> per window scope, exposed as
    /// <see cref="IWindowPlatformServices"/>, <see cref="IFileDialogs"/>, <see cref="ITextClipboard"/> and
    /// <see cref="IDesktopFileLauncher"/>, and one <see cref="IDesktopSettings"/> for the application.
    /// </summary>
    /// <remarks>
    /// A ViewModel resolved in the scope that <c>OpenWindowAsync</c> on <see cref="RunicDesktopHost"/> creates can take any of these services in
    /// its constructor. They bind to the window's native owner when the window opens, using
    /// <see cref="PlatformServices.CurrentProvider"/>, and stop when the window closes. Resolved in any other scope
    /// they stay unbound and report <see cref="PlatformUnavailableReason.OwnerUnavailable"/>.
    /// <see cref="IDesktopSettings"/> needs no window: it is a singleton created by
    /// <see cref="PlatformServices.CreateSettings"/> when first resolved, and disposed with the service provider.
    /// Existing registrations of these service types are kept.
    /// </remarks>
    public static IServiceCollection AddRunicPlatformServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(static _ => PlatformServices.CreateSettings());
        services.TryAddScoped(static _ => new DesktopWindowPlatformServices());
        services.TryAddScoped<IWindowPlatformServices>(static provider => provider.GetRequiredService<DesktopWindowPlatformServices>());
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().FileDialogs);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().Clipboard);
        services.TryAddScoped(static provider => provider.GetRequiredService<IWindowPlatformServices>().FileLauncher);
        return services;
    }
}
