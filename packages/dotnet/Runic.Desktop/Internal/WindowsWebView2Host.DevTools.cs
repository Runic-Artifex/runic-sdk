using System.Collections.Concurrent;
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
    internal const string DevToolsWindowFailedCode = "devtools-window-failed";
    private const uint MfString = 0x0, MfSeparator = 0x800;

    // The extensions installed in this process, by user data folder and folder: a second window of the same profile does
    // not install them again, which would reload them under the first window's DevTools.
    private static readonly ConcurrentDictionary<string, byte> InstalledExtensions = new(StringComparer.OrdinalIgnoreCase);

    // How long the first page waits for each extension's installation.
    internal static readonly TimeSpan ExtensionInstallDeadline = TimeSpan.FromSeconds(15);

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

    // Window thread: the menu item of DesktopMenuItem.OpenDevToolsCommandId (MenuCommandChain).
    public bool CanOpenDevTools => _devToolsEnabled && _controller is not null;

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
        if (options.EphemeralDataStore is not null || options.DevToolsExtensions.Count == 0) return;
        if (_controller?.ExtensionsRefused is { } refused)
        {
            ReportExtensionFailure(options, options.DevToolsExtensions[0], new InvalidOperationException(
                "The user data folder's WebView2 environment runs without browser extensions, for example in another process.", refused));
            return;
        }
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
            if (_options is { } options)
                ReportDevTools(options, DevToolsWindowFailedCode, $"WebView2 did not open its DevTools window: {error.Message}",
                    "Check that the WebView2 runtime is current.", "DesktopWebContentPolicy.DevTools");
        }
        return true;
    }

    // Before the first navigation, so an extension's content scripts, such as React DevTools' hook, run in the first page.
    // A folder that fails is reported and skipped; the window opens without it. Then the extensions that Runic installed
    // into this user data folder earlier and that are no longer listed are removed (D067.6).
    private async Task InstallDevToolsExtensionsAsync(WebUiEmbeddedHostOptions options)
    {
        var installed = new Dictionary<string, string>(StringComparer.Ordinal);
        var complete = true;
        foreach (var folder in options.DevToolsExtensions)
        {
            var key = (options.ProfilePath ?? string.Empty) + "\0" + folder;
            if (!InstalledExtensions.TryAdd(key, 0)) continue;
            try
            {
                // The first page waits for the installation; one that never completes must not hold the window.
                if (await _controller!.AddBrowserExtensionAsync(folder).WaitAsync(ExtensionInstallDeadline) is { Length: > 0 } id)
                    installed[id] = folder;
            }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException or COMException or TimeoutException)
            {
                InstalledExtensions.TryRemove(key, out _);
                ReportExtensionFailure(options, folder, error is TimeoutException
                    ? new TimeoutException($"WebView2 did not install it within {ExtensionInstallDeadline.TotalSeconds:0} seconds.")
                    : error);
                // A runtime without extensions, or one that does not answer, installs none of the others either.
                if (error is NotSupportedException or TimeoutException) return;
                complete = false;
            }
        }
        // Once per user data folder and process, by the window that installed the list.
        if (installed.Count > 0 || complete)
        {
            if (options.ProfilePath is { } profile && ReconciledProfiles.TryAdd(profile, 0))
                await RemoveUnlistedExtensionsAsync(options, profile, installed);
        }
    }

    // The user data folders whose record this process reconciled.
    private static readonly ConcurrentDictionary<string, byte> ReconciledProfiles = new(StringComparer.OrdinalIgnoreCase);

    private async Task RemoveUnlistedExtensionsAsync(WebUiEmbeddedHostOptions options, string profile, Dictionary<string, string> installed)
    {
        var recorded = DevToolsExtensionRecord.Read(profile);
        var (record, stale) = DevToolsExtensionRecord.Reconcile(recorded, options.DevToolsExtensions, installed);
        if (stale.Count > 0)
        {
            try
            {
                var (_, failed) = await _controller!.RemoveBrowserExtensionsAsync(stale).WaitAsync(ExtensionInstallDeadline);
                // One that could not be removed stays recorded, for the next run.
                foreach (var (id, error) in failed)
                {
                    record[id] = recorded[id];
                    ReportExtensionFailure(options, recorded[id], new InvalidOperationException("WebView2 did not remove the extension, which is no longer listed.", error));
                }
            }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException or COMException or TimeoutException)
            {
                foreach (var id in stale) record[id] = recorded[id];
                ReportExtensionFailure(options, recorded[stale[0]], new InvalidOperationException("WebView2 did not remove the extensions that are no longer listed.", error));
            }
        }
        try { DevToolsExtensionRecord.Write(profile, record); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The next run cannot tell which extensions Runic installed, so it removes none of them.
            ReportDevTools(options, DevToolsExtensionFailedCode,
                $"Runic could not record its DevTools extensions in the user data folder: {error.Message}",
                "Check that the application can write to its WebView2 user data folder.", "DesktopHostOptions.DevToolsExtensions");
        }
    }

    private static void ReportExtensionFailure(WebUiEmbeddedHostOptions options, string folder, Exception error)
    {
        var detail = error.InnerException is { } inner ? $"{error.Message} {inner.Message}" : error.Message;
        ReportDevTools(options, DevToolsExtensionFailedCode, $"The DevTools extension in '{Path.GetFileName(folder)}': {detail}",
            "Check that the folder holds an unpacked extension with its manifest.json and that the WebView2 runtime is current.",
            "DesktopHostOptions.DevToolsExtensions");
    }

    // A warning to the window's diagnostics (WebUiWindow.ReportEmbeddedDiagnostic), which go to DiagnosticSink and are
    // logged through the application's ILogger, or to Trace without one.
    private static void ReportDevTools(WebUiEmbeddedHostOptions options, string code, string message, string remediation, string option)
    {
        try
        {
            options.ReportDiagnostic?.Invoke(new DesktopDiagnostic(DesktopErrorCategory.Unavailable, code, message, Retryable: false, Remediation: remediation)
            {
                Severity = DesktopDiagnosticSeverity.Warning,
                Option = option,
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
