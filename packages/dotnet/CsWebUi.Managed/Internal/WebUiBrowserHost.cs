using System.Diagnostics;

namespace CsWebUi.Managed.Internal;

internal sealed record WebUiBrowserLaunchOptions(
    string? ProfileName,
    string? ProfilePath,
    string? ProxyServer,
    IReadOnlyList<string> CustomArguments,
    bool Kiosk,
    bool Hidden,
    uint? Width,
    uint? Height,
    uint? X,
    uint? Y);

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
        "--auto-accept-camera-and-microphone-capture",
    ];

    internal static Process Start(
        WebUiBrowserInstallation installation,
        Uri url,
        WebUiBrowserLaunchOptions options)
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

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The {installation.Browser} browser process could not be started.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    internal static IReadOnlyList<string> BuildArguments(
        WebUiBrowserInstallation installation,
        Uri url,
        WebUiBrowserLaunchOptions options)
    {
        var arguments = new List<string>();
        if (installation.IsChromiumBased)
        {
            if (!string.IsNullOrWhiteSpace(options.ProfilePath))
            {
                arguments.Add($"--user-data-dir={options.ProfilePath}");
            }

            if (options.CustomArguments.Count == 0)
            {
                arguments.AddRange(ChromiumDefaults);
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
            if (options.X is { } x && options.Y is { } y)
            {
                arguments.Add($"--window-position={x},{y}");
            }
            if (!string.IsNullOrWhiteSpace(options.ProxyServer))
            {
                arguments.Add($"--proxy-server={options.ProxyServer}");
            }
            else if (options.CustomArguments.Count == 0)
            {
                arguments.Add("--no-proxy-server");
            }

            arguments.AddRange(options.CustomArguments);
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

        if (options.CustomArguments.Count == 0)
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

        arguments.AddRange(options.CustomArguments);
        arguments.Add("--new-window");
        arguments.Add(url.AbsoluteUri);
        return arguments;
    }
}
