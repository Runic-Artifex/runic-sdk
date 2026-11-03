using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Wpf;
using Runic.Platform;
using TUnit.Core;

// B1 and B2 in WPF: WpfPlatformServices.ForWindowPresentation follows the window's activity, minimizes, hides and
// full-screens it, switches its title bar and its RunicWebViews' scheme, also before the window has a handle and for a
// view added later, logs a handler that throws and raises a change a handler causes after that handler.
internal sealed partial class WindowPresentationChecks
{
    [Test, NotInParallel("WpfSuite", Order = 41)]
    public async Task ThePresentationFollowsAndControlsTheWindow()
    {
        var log = new EventLog();
        var window = new Window { Width = 320, Height = 200, Title = "Runic WPF presentation" };
        var presentation = WpfPlatformServices.ForWindowPresentation(window, log);
        Program.Check(ReferenceEquals(presentation, WpfPlatformServices.ForWindowPresentation(window)),
            "A second call created another presentation for the window.");
        Program.Check(presentation.Features.HasFlag(RunicWindowPresentationFeatures.Activity | RunicWindowPresentationFeatures.PerWindowColorScheme),
            $"Features: {presentation.Features}.");
        try
        {
            // Before the window has a handle: the title bar follows once it has one.
            Program.Check(await presentation.SetColorSchemeAsync(DesktopColorScheme.Dark) is PlatformResult<PlatformUnit>.Success,
                "Setting the scheme before the window had a handle failed.");
            window.Show();
            var handle = new WindowInteropHelper(window).Handle;
            Program.Check(DarkMode(handle) == 1, $"The title bar of a window shown after the scheme was set is not dark: {DarkMode(handle)}.");
            Program.Check(presentation.ColorScheme == DesktopColorScheme.Dark, "The presentation does not report its scheme.");
            // A view added after the scheme was set opens with it.
            using var child = new RunicWebView();
            window.Content = child;
            Program.Check(child.OpeningColorScheme(DesktopColorScheme.Light) == (DesktopColorScheme.Dark, true),
                "A RunicWebView added later does not take the window's scheme.");

            // A handler that throws is logged; a change a handler causes comes after that handler.
            List<string> order = [];
            presentation.ActivityChanged += (_, _) => throw new InvalidOperationException("handler failure");
            presentation.ActivityChanged += (_, e) =>
            {
                order.Add($"start {e.Activity.IsMinimized}");
                if (e.Activity.IsMinimized) window.WindowState = WindowState.Normal;
                order.Add($"end {e.Activity.IsMinimized}");
            };
            Program.Check(await presentation.MinimizeAsync() is PlatformResult<PlatformUnit>.Success, "Minimizing failed.");
            Program.Check(order.Take(2).SequenceEqual(["start True", "end True"]) && order.Contains("start False"),
                $"Order: {string.Join(", ", order)}.");
            Program.Check(log.Events.Contains(1130), "A throwing handler was not logged as event 1130.");

            Program.Check(await presentation.SetVisibleAsync(false) is PlatformResult<PlatformUnit>.Success && !presentation.IsVisible && !window.IsVisible,
                "Hiding did not hide the window.");
            Program.Check(await presentation.SetVisibleAsync(true) is PlatformResult<PlatformUnit>.Success && presentation.IsVisible,
                "Showing did not show the window.");
            Program.Check(await presentation.SetFullScreenAsync(true) is PlatformResult<PlatformUnit>.Success
                && window.WindowStyle == WindowStyle.None && window.WindowState == WindowState.Maximized, "Full screen did not fill the monitor.");
            Program.Check(await presentation.SetFullScreenAsync(false) is PlatformResult<PlatformUnit>.Success
                && window.WindowStyle == WindowStyle.SingleBorderWindow && window.WindowState == WindowState.Normal, "Leaving full screen did not restore the window.");
            Program.Check(await presentation.SetDocumentEditedAsync(true) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
                "WPF reported an edited mark.");
            await presentation.SetColorSchemeAsync(DesktopColorScheme.Light);
            Program.Check(DarkMode(handle) == 0, "The title bar did not turn light.");
        }
        finally { window.Close(); }
        Program.Check(presentation.Activity == RunicWindowActivity.Closed, $"A closed window reported {presentation.Activity}.");
        Program.Check(await presentation.PresentAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed },
            "A closed window did not report the owner closed.");
    }

    // DWMWA_USE_IMMERSIVE_DARK_MODE, or its number before Windows 10 20H1.
    private static int DarkMode(nint handle) =>
        DwmGetWindowAttribute(handle, 20, out var value, sizeof(int)) >= 0 || DwmGetWindowAttribute(handle, 19, out value, sizeof(int)) >= 0 ? value : -1;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    private sealed class EventLog : ILoggerFactory
    {
        public List<int> Events { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger(EventLog owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Events.Add(eventId.Id);
        }
    }
}
