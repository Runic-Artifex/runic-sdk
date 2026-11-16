using System;
using System.Collections.Generic;
using System.IO;

namespace Runic.Application.Tool;

/// <summary>
/// The Runic DevTools extension (#1044): an unpacked Manifest V3 Chrome DevTools extension with a "Runic" panel and an
/// Elements pane, which reads Runic Inspect's development API on pages that vite-plugin-runic's development client
/// serves. It ships in this tool's package, next to its assembly, so its path is stable for an installed tool version.
/// <c>dev</c> prints it and writes it to the session file (<c>devtoolsExtension</c>), where a browser launch can pass it
/// to <c>--load-extension</c> and a WebView2 Window in Development can register it.
/// </summary>
internal static class DevToolsExtension
{
    internal const string DirectoryName = "devtools-extension";

    /// <summary>
    /// The extension's id, which the manifest's public <c>key</c> fixes wherever the folder is: the first 16 bytes of the
    /// key's SHA-256 as the letters a-p. No private key exists; nothing is signed or published.
    /// </summary>
    internal const string Id = "cddbfcinbkaindmfgcfdgapddkjeadgh";

    /// <summary>The unpacked extension's directory beside the tool, or null when the tool was built without it.</summary>
    internal static string? Find(string? baseDirectory = null)
    {
        string directory = Path.GetFullPath(Path.Combine(baseDirectory ?? AppContext.BaseDirectory, DirectoryName));
        return File.Exists(Path.Combine(directory, "manifest.json")) ? directory : null;
    }

    /// <summary>
    /// The variable from which a Runic Desktop Window on Windows loads unpacked extensions into WebView2, in Development
    /// only (<c>DevToolsExtensionFolders</c> in Runic.Desktop, sdk#1043). Its entries are separated by <c>;</c>.
    /// </summary>
    internal const string WindowVariable = "RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS";

    /// <summary>
    /// The extension folder that <c>dev</c> adds to the Window's <see cref="WindowVariable"/>, or null when it adds none:
    /// only on Windows, where Runic Desktop's WebView2 loads it; not with <c>--no-devtools-extension</c>, under
    /// <c>--browser</c> (no WebView2) or for a CS-WebUI-only Window; and only when a development server serves Runic
    /// Inspect, which the extension reads.
    /// </summary>
    internal static string? ForWindow(DevProjectConfiguration configuration, DevOptions options, string? extension, bool windows)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        return windows && options.DevToolsExtension && !options.Browser && !configuration.RunsOnCsWebUiOnly &&
            configuration.HasDevelopmentServer && options.WatchFrontend
            ? extension
            : null;
    }

    /// <summary>
    /// The Window's <see cref="WindowVariable"/> with <paramref name="extension"/> appended to the folders the developer
    /// listed (<paramref name="read"/> gives the Window's environment as dev composes it), or null to leave the variable
    /// as it is: outside Development (<c>DOTNET_ENVIRONMENT</c>, else <c>ASPNETCORE_ENVIRONMENT</c>, as the host decides),
    /// and when the list names the folder already, in any case and with or without a trailing separator.
    /// </summary>
    internal static string? AppendTo(string extension, Func<string, string?> read)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension);
        ArgumentNullException.ThrowIfNull(read);
        string? environment = read("DOTNET_ENVIRONMENT");
        if (string.IsNullOrEmpty(environment)) environment = read("ASPNETCORE_ENVIRONMENT");
        if (!string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase)) return null;
        string listed = read(WindowVariable) ?? "";
        var folders = new List<string>(listed.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        if (folders.Exists(folder => SameFolder(folder, extension))) return null;
        folders.Add(extension);
        return string.Join(';', folders);
    }

    private static bool SameFolder(string left, string right) =>
        string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The line <c>dev</c> prints when the page has a development server that serves Runic Inspect.</summary>
    internal static string Hint(string directory) =>
        $"[dev] DevTools extension: {directory} (Chrome or Edge: Load unpacked; adds a Runic panel and an Elements pane)";

    /// <summary>
    /// How the Window's WebView2 gets the extension, which <c>dev</c> prints after <see cref="Hint"/> on Windows: loaded
    /// by default, or left out with <c>--no-devtools-extension</c>.
    /// </summary>
    internal static string WindowHint(bool loaded) => loaded
        ? $"[dev] DevTools extension: the Window's WebView2 loads it in Development ({WindowVariable}); --no-devtools-extension leaves it out."
        : "[dev] DevTools extension: not added to the Window's WebView2 (--no-devtools-extension).";

    /// <summary>
    /// Under <c>--browser</c>: branded Chrome ignores <c>--load-extension</c> (since Chrome 137), so the extension is
    /// loaded once by hand in the browser profile the IDE's launch keeps; Chromium and Chrome for Testing take the flag.
    /// </summary>
    internal static string BrowserHint(string directory) =>
        $"[dev] DevTools extension in the IDE's browser: open chrome://extensions (edge://extensions), turn on Developer mode, " +
        $"choose Load unpacked and pick {directory}; the debug profile keeps it. With Chromium or Chrome for Testing, add " +
        $"\"runtimeArgs\": [\"--load-extension={directory.Replace("\\", "/", StringComparison.Ordinal)}\"] to the launch configuration instead.";
}
