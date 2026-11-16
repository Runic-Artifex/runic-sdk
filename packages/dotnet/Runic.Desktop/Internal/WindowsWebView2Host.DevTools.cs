using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// The developer tools of a WebView2 window in Development (sdk#1043): DesktopWindow.OpenDevToolsAsync, an Open DevTools
// item in the window's system menu, and the DevTools extensions of DesktopHostOptions.DevToolsExtensions. Nothing here
// runs for a window whose developer tools are off, as in production.
internal sealed partial class WindowsWebView2Host
{
    // A system menu command: below 0xF000, with the low four bits clear (WM_SYSCOMMAND uses them).
    internal const uint OpenDevToolsSystemCommand = 0x0DE0;
    internal const string OpenDevToolsTitle = "Open DevTools";
    internal const string DevToolsExtensionFailedCode = "devtools-extension-failed";
    private const uint MfString = 0x0, MfSeparator = 0x800;

    // The extensions installed in this process, by user data folder and folder: a second window of the same profile does
    // not install them again, which would reload them under the first window's DevTools.
    private static readonly ConcurrentDictionary<string, byte> InstalledExtensions = new(StringComparer.OrdinalIgnoreCase);

    // Window thread: whether WebContentRules enabled the developer tools of this window.
    private bool _devToolsEnabled;

    public ValueTask<bool> OpenDevToolsAsync(CancellationToken cancellationToken = default)
    {
        var opened = false;
        var work = InvokeAsync(() => opened = OpenDevTools(), cancellationToken);
        return work.IsCompletedSuccessfully ? ValueTask.FromResult(opened) : AwaitAsync(work);

        async ValueTask<bool> AwaitAsync(ValueTask pending)
        {
            await pending.ConfigureAwait(false);
            return opened;
        }
    }

    // Window thread.
    private bool OpenDevTools()
    {
        if (!_devToolsEnabled || _controller is not { } controller) return false;
        controller.OpenDevToolsWindow();
        return true;
    }

    // Window thread, once the controller applied the web content policy.
    private async Task EnableDevToolsAsync(WebUiEmbeddedHostOptions options, bool devToolsEnabled)
    {
        _devToolsEnabled = devToolsEnabled;
        if (!devToolsEnabled) return;
        AddDevToolsSystemMenuItem();
        if (options.EphemeralDataStore is null && options.DevToolsExtensions.Count > 0)
            await InstallDevToolsExtensionsAsync(options);
    }

    // Alt+Space or a right click on the title bar; a frameless window has the menu too, through Alt+Space.
    private void AddDevToolsSystemMenuItem()
    {
        var menu = Native.GetSystemMenu(_window, false);
        if (menu == 0) return;
        _ = Native.AppendMenu(menu, MfSeparator, 0, null);
        _ = Native.AppendMenu(menu, MfString, OpenDevToolsSystemCommand, OpenDevToolsTitle);
    }

    // WM_SYSCOMMAND of the system menu item; the window procedure must not throw.
    private bool RunsDevToolsSystemCommand(nuint command)
    {
        if ((command & 0xFFF0) != OpenDevToolsSystemCommand) return false;
        try { _ = OpenDevTools(); }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            Trace.TraceWarning($"WebView2 did not open its DevTools window: {error.Message}");
        }
        return true;
    }

    // Before the first navigation, so an extension's content scripts, such as React DevTools' hook, run in the first page.
    // A folder that fails is reported and skipped; the window opens without it.
    private async Task InstallDevToolsExtensionsAsync(WebUiEmbeddedHostOptions options)
    {
        foreach (var folder in options.DevToolsExtensions)
        {
            var key = (options.ProfilePath ?? string.Empty) + "\0" + folder;
            if (!InstalledExtensions.TryAdd(key, 0)) continue;
            try
            {
                await _controller!.AddBrowserExtensionAsync(folder);
            }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException or COMException)
            {
                InstalledExtensions.TryRemove(key, out _);
                ReportExtensionFailure(options, folder, error);
                // A runtime without extensions installs none of the others either.
                if (error is NotSupportedException) return;
            }
        }
    }

    private static void ReportExtensionFailure(WebUiEmbeddedHostOptions options, string folder, Exception error)
    {
        var detail = error.InnerException is { } inner ? $"{error.Message} {inner.Message}" : error.Message;
        var message = $"The DevTools extension in '{Path.GetFileName(folder)}' was not loaded: {detail}";
        Trace.TraceWarning(message);
        try
        {
            options.ReportDiagnostic?.Invoke(new DesktopDiagnostic(DesktopErrorCategory.Unavailable, DevToolsExtensionFailedCode, message,
                Retryable: false,
                Remediation: "Check that the folder holds an unpacked extension with its manifest.json and that the WebView2 runtime is current.")
            {
                Severity = DesktopDiagnosticSeverity.Warning,
                Option = "DesktopHostOptions.DevToolsExtensions",
            });
        }
        catch (Exception sinkError) when (sinkError is not OutOfMemoryException) { }
    }

    private static partial class Native
    {
        [LibraryImport("user32.dll")]
        internal static partial nint GetSystemMenu(nint window, [MarshalAs(UnmanagedType.Bool)] bool revert);

        [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AppendMenu(nint menu, uint flags, nuint item, string? title);
    }
}
