using System;
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

    /// <summary>The unpacked extension's directory beside the tool, or null when the tool was built without it.</summary>
    internal static string? Find(string? baseDirectory = null)
    {
        string directory = Path.GetFullPath(Path.Combine(baseDirectory ?? AppContext.BaseDirectory, DirectoryName));
        return File.Exists(Path.Combine(directory, "manifest.json")) ? directory : null;
    }

    /// <summary>The line <c>dev</c> prints when the page has a development server that serves Runic Inspect.</summary>
    internal static string Hint(string directory) =>
        $"[dev] DevTools extension: {directory} (Chrome or Edge: Load unpacked; adds a Runic panel and an Elements pane)";
}
