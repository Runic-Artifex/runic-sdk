using Microsoft.Extensions.Logging;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

// The window's locale (WindowContentSession.Locale) reaching the native window while it is open: RunicDesktopHost's
// Windows and DesktopPlatformServices.AttachWindowServices.
internal static class DesktopWindowLocaleFollow
{
    // A change of the locale changes the native direction of the open window; a null locale returns it to the platform's.
    // Nothing on macOS, where AppKit lays windows out for the bundle's localization, or for a window without
    // DesktopWindowCapabilities.LayoutDirection. It ends when the window closes or the result is disposed.
    internal static IDisposable FollowDirection(WindowContentSession content, DesktopWindow window, ILogger logger)
    {
        if (OperatingSystem.IsMacOS() || !window.Capabilities.HasFlag(DesktopWindowCapabilities.LayoutDirection)) return Nothing.Instance;
        // One change at a time, each with the direction of the locale it finds, not of the one that raised it: changes
        // from several threads apply in order, and the window ends with the direction of the last locale.
        var gate = new SemaphoreSlim(1, 1);
        void OnLocaleChanged(object? sender, EventArgs args) => _ = ApplyAsync();
        async Task ApplyAsync()
        {
            try
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!window.IsOpen) return;
                    var direction = DirectionOf(content.Locale);
                    if (direction != window.LayoutDirection)
                        await window.SetLayoutDirectionAsync(direction).ConfigureAwait(false);
                }
                finally { gate.Release(); }
            }
            catch (ObjectDisposedException) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                DesktopLog.LayoutDirectionFailed(logger, error, error.GetType().Name);
            }
        }
        var follow = new Follow(content, window, OnLocaleChanged);
        // A change while the window opened, after its options took the locale, applies now.
        _ = ApplyAsync();
        return follow;
    }

    // The pages the surface serves itself, such as Resource Not Available, are in the window's language. It ends when the
    // result is disposed.
    internal static IDisposable FollowPageLanguage(WindowContentSession content, DesktopSurface surface)
    {
        void OnLocaleChanged(object? sender, EventArgs args) => surface.PageLanguage = content.Locale;
        content.LocaleChanged += OnLocaleChanged;
        surface.PageLanguage = content.Locale;
        return new Unsubscribe(() => content.LocaleChanged -= OnLocaleChanged);
    }

    // The native direction of a locale (WindowLocaleDirection), or the platform's without one.
    internal static DesktopLayoutDirection DirectionOf(string? locale) =>
        locale is null ? DesktopLayoutDirection.Default : Runic.Desktop.Internal.WindowLocaleDirection.Of(locale);

    private sealed class Follow : IDisposable
    {
        private readonly WindowContentSession _content;
        private readonly DesktopWindow _window;
        private readonly EventHandler _changed;
        private int _disposed;

        internal Follow(WindowContentSession content, DesktopWindow window, EventHandler changed)
        {
            (_content, _window, _changed) = (content, window, changed);
            content.LocaleChanged += changed;
            window.Closed += OnClosed;
            // A window that closed meanwhile raised Closed before this handler was added.
            if (!window.IsOpen) Dispose();
        }

        private void OnClosed(object? sender, EventArgs args) => Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _content.LocaleChanged -= _changed;
            _window.Closed -= OnClosed;
        }
    }

    private sealed class Unsubscribe(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }

    private sealed class Nothing : IDisposable
    {
        internal static readonly Nothing Instance = new();

        public void Dispose() { }
    }
}
