using System.Diagnostics;

namespace Runic.Desktop.Internal;

internal sealed record WebUiBrowserLaunchOptions(
    string? ProfileName,
    string? ProfilePath,
    string? ProxyServer,
    IReadOnlyList<string> BrowserArguments,
    bool Kiosk,
    bool Hidden,
    uint? Width,
    uint? Height,
    int? X,
    int? Y,
    DesktopPermissionGrant AllowedPermissions = DesktopPermissionGrant.None,
    int? RemoteDebuggingPort = null);

internal static class WebUiBrowserHost
{
    private static readonly string[] ChromiumDefaults =
    [
        "--no-first-run",
        "--safe-mode",
        "--disable-extensions",
        "--disable-background-mode",
        "--disable-plugins",
        "--disable-plugins-discovery",
        "--disable-translate",
        "--disable-features=Translate",
        "--bwsi",
        "--disable-sync",
        "--disable-sync-preferences",
        "--disable-component-update",
        "--allow-insecure-localhost",
    ];

    internal static Process Start(
        WebUiBrowserInstallation installation,
        Uri url,
        WebUiBrowserLaunchOptions options,
        out Task outputClosed)
    {
        var startInfo = new ProcessStartInfo(installation.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = options.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in BuildArguments(installation, url, options))
        {
            startInfo.ArgumentList.Add(argument);
        }
        PrepareEnvironment(startInfo.Environment, OperatingSystem.IsWindows());

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The {installation.Browser} browser process could not be started.");
        // Helpers inherit these pipes. Their EOF is a shutdown barrier even when
        // the parent exits first; discard output instead of retaining it forever.
        outputClosed = Task.WhenAll(DrainAsync(process.StandardOutput), DrainAsync(process.StandardError));
        return process;
    }

    // Windows sets __COMPAT_LAYER for some starts from Explorer, and a user can set it (`set __COMPAT_LAYER=Installer`).
    // Edge that inherits it starts a new copy of itself and exits (#1097). Only the browser's environment loses it;
    // the application's own process environment is not changed.
    internal const string CompatLayerVariable = "__COMPAT_LAYER";

    // The switch that the copy Edge starts after a compatibility-layer relaunch carries; given at once, Edge does not
    // relaunch. Other browsers ignore it, but it is passed to Edge alone.
    internal const string EdgeSkipCompatLayerRelaunch = "--edge-skip-compat-layer-relaunch";

    internal static void PrepareEnvironment(IDictionary<string, string?> environment, bool windows)
    {
        if (!windows)
        {
            return;
        }
        // Windows environment names are case-insensitive.
        foreach (var name in environment.Keys
            .Where(static name => string.Equals(name, CompatLayerVariable, StringComparison.OrdinalIgnoreCase))
            .ToArray())
        {
            environment.Remove(name);
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
    }

    internal static IReadOnlyList<string> BuildArguments(
        WebUiBrowserInstallation installation,
        Uri url,
        WebUiBrowserLaunchOptions options) =>
        BuildArguments(installation, url, options, OperatingSystem.IsWindows());

    internal static IReadOnlyList<string> BuildArguments(
        WebUiBrowserInstallation installation,
        Uri url,
        WebUiBrowserLaunchOptions options,
        bool windows)
    {
        var arguments = new List<string>();
        if (installation.IsChromiumBased)
        {
            if (!string.IsNullOrWhiteSpace(options.ProfilePath))
            {
                arguments.Add($"--user-data-dir={options.ProfilePath}");
            }

            // Not one of the defaults that the application's own BrowserArguments replace: without it, a compatibility
            // layer in an inherited environment makes Edge hand off to a new copy of itself (#1097).
            if (windows && installation.Browser == WebUiBrowser.Edge
                && !options.BrowserArguments.Contains(EdgeSkipCompatLayerRelaunch, StringComparer.OrdinalIgnoreCase))
            {
                arguments.Add(EdgeSkipCompatLayerRelaunch);
            }

            if (options.BrowserArguments.Count == 0)
            {
                arguments.AddRange(ChromiumDefaults);
            }

            // Chromium cannot limit an automatic grant to one origin, so with MediaCapture it asks the user, who sees
            // the requesting origin (SEC-11); without it, permission prompts are denied.
            if ((options.AllowedPermissions & DesktopPermissionGrant.MediaCapture) == 0)
            {
                arguments.Add("--deny-permission-prompts");
            }

            if (options.Kiosk)
            {
                arguments.Add("--chrome-frame");
                arguments.Add("--kiosk");
            }
            if (options.Hidden)
            {
                arguments.Add("--headless=new");
            }
            if (options.Width is { } width && options.Height is { } height)
            {
                arguments.Add($"--window-size={width},{height}");
            }
            // Installed browsers cannot honour a negative position; option validation reports it as ignored.
            if (options.X is int x and >= 0 && options.Y is int y and >= 0)
            {
                arguments.Add($"--window-position={x},{y}");
            }
            if (!string.IsNullOrWhiteSpace(options.ProxyServer))
            {
                arguments.Add($"--proxy-server={options.ProxyServer}");
            }
            else if (options.BrowserArguments.Count == 0)
            {
                arguments.Add("--no-proxy-server");
            }

            // D070.7: Development's CDP endpoint (WebUiRemoteDebugging), beside the defaults or the application's own
            // arguments; an application that names the switch keeps its own value, and there is never a second one.
            if (options.RemoteDebuggingPort is int debuggingPort
                && !string.IsNullOrWhiteSpace(options.ProfilePath)
                && !WebUiRemoteDebugging.NamesPort(options.BrowserArguments))
            {
                arguments.Add(WebUiRemoteDebugging.Argument(debuggingPort));
            }

            arguments.AddRange(options.BrowserArguments);
            arguments.Add($"--app={url.AbsoluteUri}");
            return arguments;
        }

        if (!string.IsNullOrWhiteSpace(options.ProfilePath))
        {
            arguments.Add("--profile");
            arguments.Add(options.ProfilePath);
            arguments.Add("--new-instance");
            arguments.Add("--no-remote");
        }
        else if (!string.IsNullOrWhiteSpace(options.ProfileName))
        {
            arguments.Add("-P");
            arguments.Add(options.ProfileName);
        }

        if (options.BrowserArguments.Count == 0)
        {
            arguments.Add("-purgecaches");
        }
        if (options.Kiosk)
        {
            arguments.Add("--kiosk");
        }
        if (options.Hidden)
        {
            arguments.Add("--headless");
        }
        if (options.Width is { } firefoxWidth && options.Height is { } firefoxHeight)
        {
            arguments.Add("--width");
            arguments.Add(firefoxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            arguments.Add("--height");
            arguments.Add(firefoxHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.AddRange(options.BrowserArguments);
        arguments.Add("--new-window");
        arguments.Add(url.AbsoluteUri);
        return arguments;
    }
}
