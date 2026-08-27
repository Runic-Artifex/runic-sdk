using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CsWebUi.Managed.Internal;

internal sealed record WebUiBrowserInstallation(
    WebUiBrowser Browser,
    string ExecutablePath,
    bool IsChromiumBased);

internal static class WebUiBrowserDiscovery
{
    private static readonly WebUiBrowser[] WindowsOrder =
    [
        WebUiBrowser.Chrome, WebUiBrowser.Edge, WebUiBrowser.Epic, WebUiBrowser.Vivaldi,
        WebUiBrowser.Brave, WebUiBrowser.Firefox, WebUiBrowser.Yandex, WebUiBrowser.Chromium,
    ];

    private static readonly WebUiBrowser[] UnixOrder =
    [
        WebUiBrowser.Chrome, WebUiBrowser.Edge, WebUiBrowser.Chromium, WebUiBrowser.Epic,
        WebUiBrowser.Vivaldi, WebUiBrowser.Brave, WebUiBrowser.Firefox, WebUiBrowser.Yandex,
    ];

    private static readonly WebUiBrowser[] ChromiumOrder =
    [
        WebUiBrowser.Chrome, WebUiBrowser.Edge, WebUiBrowser.Epic, WebUiBrowser.Vivaldi,
        WebUiBrowser.Brave, WebUiBrowser.Yandex, WebUiBrowser.Chromium,
    ];

    internal static WebUiBrowserInstallation? Find(WebUiBrowser browser, string? customFolder)
    {
        if (browser == WebUiBrowser.AnyBrowser)
        {
            return FindFirst(OperatingSystem.IsWindows() ? WindowsOrder : UnixOrder, customFolder);
        }

        if (browser == WebUiBrowser.ChromiumBased)
        {
            return FindFirst(ChromiumOrder, customFolder);
        }

        if (browser is WebUiBrowser.NoBrowser or WebUiBrowser.Safari or WebUiBrowser.Opera or WebUiBrowser.WebView)
        {
            return null;
        }

        foreach (var candidate in GetCandidates(browser, customFolder))
        {
            if (IsExecutable(candidate))
            {
                return new WebUiBrowserInstallation(browser, Path.GetFullPath(candidate), IsChromium(browser));
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> GetCandidates(WebUiBrowser browser, string? customFolder)
    {
        var names = GetExecutableNames(browser);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(customFolder))
        {
            foreach (var name in names)
            {
                candidates.Add(Path.Combine(customFolder, name));
            }

            if (OperatingSystem.IsMacOS())
            {
                AddMacCandidates(candidates, browser, customFolder);
            }

            return candidates;
        }

        foreach (var directory in GetPathDirectories())
        {
            foreach (var name in names)
            {
                candidates.Add(Path.Combine(directory, name));
            }
        }

        if (OperatingSystem.IsWindows())
        {
            AddWindowsCandidates(candidates, browser);
            AddWindowsRegistryCandidates(candidates, browser);
        }
        else if (OperatingSystem.IsMacOS())
        {
            AddMacCandidates(candidates, browser, "/Applications");
            var userApplications = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Applications");
            AddMacCandidates(candidates, browser, userApplications);
        }
        else
        {
            foreach (var directory in new[] { "/usr/bin", "/usr/local/bin", "/snap/bin", "/opt/bin" })
            {
                foreach (var name in names)
                {
                    candidates.Add(Path.Combine(directory, name));
                }
            }
        }

        return candidates.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static bool IsChromium(WebUiBrowser browser) => browser is
        WebUiBrowser.Chrome or WebUiBrowser.Edge or WebUiBrowser.Chromium or WebUiBrowser.Brave or
        WebUiBrowser.Vivaldi or WebUiBrowser.Epic or WebUiBrowser.Yandex;

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static WebUiBrowserInstallation? FindFirst(IEnumerable<WebUiBrowser> browsers, string? customFolder)
    {
        foreach (var browser in browsers)
        {
            var installation = Find(browser, customFolder);
            if (installation is not null)
            {
                return installation;
            }
        }

        return null;
    }

    private static string[] GetExecutableNames(WebUiBrowser browser)
    {
        if (OperatingSystem.IsWindows())
        {
            return browser switch
            {
                WebUiBrowser.Chrome or WebUiBrowser.Chromium => ["chrome.exe"],
                WebUiBrowser.Edge => ["msedge.exe"],
                WebUiBrowser.Epic => ["epic.exe"],
                WebUiBrowser.Vivaldi => ["vivaldi.exe"],
                WebUiBrowser.Brave => ["brave.exe", "brave-browser.exe"],
                WebUiBrowser.Firefox => ["firefox.exe"],
                WebUiBrowser.Yandex => ["browser.exe"],
                _ => [],
            };
        }

        return browser switch
        {
            WebUiBrowser.Chrome => ["google-chrome", "google-chrome-stable"],
            WebUiBrowser.Edge => ["microsoft-edge", "microsoft-edge-stable", "microsoft-edge-beta"],
            WebUiBrowser.Epic => ["epic"],
            WebUiBrowser.Vivaldi => ["vivaldi", "vivaldi-stable", "vivaldi-snapshot"],
            WebUiBrowser.Brave => ["brave", "brave-browser", "brave-browser-stable", "brave-browser-beta", "brave-browser-nightly"],
            WebUiBrowser.Firefox => ["firefox"],
            WebUiBrowser.Yandex => ["yandex-browser"],
            WebUiBrowser.Chromium => ["chromium", "chromium-browser"],
            _ => [],
        };
    }

    private static string[] GetPathDirectories() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AddWindowsCandidates(List<string> candidates, WebUiBrowser browser)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] suffixes = browser switch
        {
            WebUiBrowser.Chrome => [@"Google\Chrome\Application\chrome.exe"],
            WebUiBrowser.Edge => [@"Microsoft\Edge\Application\msedge.exe"],
            WebUiBrowser.Epic => [@"Epic Privacy Browser\Application\epic.exe"],
            WebUiBrowser.Vivaldi => [@"Vivaldi\Application\vivaldi.exe"],
            WebUiBrowser.Brave => [@"BraveSoftware\Brave-Browser\Application\brave.exe"],
            WebUiBrowser.Firefox => [@"Mozilla Firefox\firefox.exe"],
            WebUiBrowser.Yandex => [@"Yandex\YandexBrowser\Application\browser.exe"],
            WebUiBrowser.Chromium => [@"Chromium\Application\chrome.exe"],
            _ => [],
        };

        foreach (var root in new[] { programFiles, programFilesX86, local }.Where(static path => path.Length > 0))
        {
            foreach (var suffix in suffixes)
            {
                candidates.Add(Path.Combine(root, suffix));
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsRegistryCandidates(List<string> candidates, WebUiBrowser browser)
    {
        if (browser == WebUiBrowser.Chromium)
        {
            return;
        }

        foreach (var executable in GetExecutableNames(browser))
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var key = baseKey.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executable}");
                        if (key?.GetValue(null) is string path && path.Length > 0)
                        {
                            candidates.Add(path.Trim('"'));
                        }
                    }
                    catch (System.Security.SecurityException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
    }

    private static void AddMacCandidates(List<string> candidates, WebUiBrowser browser, string applicationsFolder)
    {
        var (bundle, executable) = browser switch
        {
            WebUiBrowser.Chrome => ("Google Chrome.app", "Google Chrome"),
            WebUiBrowser.Edge => ("Microsoft Edge.app", "Microsoft Edge"),
            WebUiBrowser.Epic => ("Epic.app", "Epic"),
            WebUiBrowser.Vivaldi => ("Vivaldi.app", "Vivaldi"),
            WebUiBrowser.Brave => ("Brave Browser.app", "Brave Browser"),
            WebUiBrowser.Firefox => ("Firefox.app", "firefox"),
            WebUiBrowser.Yandex => ("Yandex.app", "Yandex"),
            WebUiBrowser.Chromium => ("Chromium.app", "Chromium"),
            _ => (string.Empty, string.Empty),
        };
        if (bundle.Length > 0)
        {
            candidates.Add(Path.Combine(applicationsFolder, bundle, "Contents", "MacOS", executable));
        }
    }
}
