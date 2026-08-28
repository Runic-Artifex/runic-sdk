using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Runic.Desktop.Internal;

namespace Runic.Desktop;

/// <summary>Provides process-wide configuration and lifetime operations for Runic Desktop windows.</summary>
public static class WebUiApplication
{
    private static readonly object Gate = new();
    private static readonly HashSet<WebUiWindow> RunningWindows = [];
    private static readonly HashSet<string> GeneratedProfiles = new(StringComparer.Ordinal);
    private static TaskCompletionSource _allClosed = CompletedSource();
    private static string _defaultRootFolder = Path.GetFullPath(Environment.CurrentDirectory);
    private static string? _browserFolder;
    private static IWebUiEmbeddedHostFactory _embeddedHostFactory = WebUiEmbeddedHostFactory.Instance;
    private static long _connectionTimeoutTicks = TimeSpan.FromSeconds(15).Ticks;
    private static int _showWaitConnection = 1;
    private static int _multiClient;
    private static int _useCookies = 1;

    /// <summary>Gets whether at least one Runic Desktop server is running.</summary>
    public static bool IsRunning
    {
        get
        {
            lock (Gate)
            {
                return RunningWindows.Count > 0;
            }
        }
    }

    /// <summary>Sets a process-wide Runic Desktop configuration option.</summary>
    public static void SetConfiguration(WebUiConfiguration configuration, bool enabled)
    {
        switch (configuration)
        {
            case WebUiConfiguration.MultiClient:
                Volatile.Write(ref _multiClient, enabled ? 1 : 0);
                break;
            case WebUiConfiguration.UseCookies:
                Volatile.Write(ref _useCookies, enabled ? 1 : 0);
                break;
            case WebUiConfiguration.ShowWaitConnection:
                Volatile.Write(ref _showWaitConnection, enabled ? 1 : 0);
                break;
            case WebUiConfiguration.UiEventBlocking:
            case WebUiConfiguration.FolderMonitor:
            case WebUiConfiguration.AsynchronousResponse:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(configuration));
        }
    }

    /// <summary>Sets the maximum number of seconds a show call waits for bridge authentication.</summary>
    public static void SetConnectionTimeout(nuint seconds)
    {
        var clampedSeconds = seconds > 60 ? 60 : (long)seconds;
        var timeout = clampedSeconds == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(clampedSeconds);
        Volatile.Write(ref _connectionTimeoutTicks, timeout.Ticks);
    }

    /// <summary>Sets an optional folder searched before system browser locations.</summary>
    public static void SetBrowserFolder(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (Gate)
        {
            _browserFolder = path.Length == 0 ? null : Path.GetFullPath(path);
        }
    }

    /// <summary>Gets whether a supported browser can be discovered.</summary>
    public static bool BrowserExists(WebUiBrowser browser)
        => browser == WebUiBrowser.WebView
            ? EmbeddedWebViewExists
            : WebUiBrowserDiscovery.Find(browser, GetBrowserFolder()) is not null;

    /// <summary>Gets whether the configured embedded WebView host is available.</summary>
    public static bool EmbeddedWebViewExists
    {
        get
        {
            IWebUiEmbeddedHostFactory factory;
            lock (Gate)
            {
                factory = _embeddedHostFactory;
            }
            return factory.IsSupported;
        }
    }

    /// <summary>Gets whether the operating system currently uses a high-contrast theme.</summary>
    public static bool IsHighContrast => WebUiSystemTheme.IsHighContrast;

    /// <summary>Sets a custom embedded host factory, or restores the platform default when null.</summary>
    public static void SetEmbeddedHostFactory(IWebUiEmbeddedHostFactory? factory)
    {
        lock (Gate)
        {
            if (RunningWindows.Any(static window => window.HasEmbeddedHost))
            {
                throw new InvalidOperationException("Close all embedded WebView windows before changing the host factory.");
            }
            _embeddedHostFactory = factory ?? WebUiEmbeddedHostFactory.Instance;
        }
    }

    /// <summary>Opens a URL through the operating system's default URL handler.</summary>
    public static void OpenUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Deletes generated browser profiles that are not owned by a running window.</summary>
    public static void DeleteAllProfiles()
    {
        string[] profiles;
        lock (Gate)
        {
            var activeProfiles = RunningWindows
                .Select(static window => window.GeneratedProfilePath)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            profiles = [.. GeneratedProfiles.Where(path => !activeProfiles.Contains(path))];
        }

        foreach (var profile in profiles)
        {
            _ = TryDeleteGeneratedProfile(profile);
        }
    }

    /// <summary>Closes all windows and removes managed browser profiles.</summary>
    public static void Clean()
    {
        Exit();
        DeleteAllProfiles();
    }

    /// <summary>Gets an available loopback TCP port.</summary>
    public static nuint GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return checked((nuint)((IPEndPoint)listener.LocalEndpoint).Port);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Sets the root folder copied by subsequently created managed windows.</summary>
    public static void SetDefaultRootFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"The Runic Desktop root folder does not exist: {fullPath}");
        }

        lock (Gate)
        {
            _defaultRootFolder = fullPath;
        }
    }

    /// <summary>Blocks until all running Runic Desktop windows have closed.</summary>
    public static void Wait() => WaitAsync().GetAwaiter().GetResult();

    /// <summary>Asynchronously waits until all running Runic Desktop windows have closed.</summary>
    public static Task WaitAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        WebUiWindow[] windows;
        lock (Gate)
        {
            task = _allClosed.Task;
            windows = [.. RunningWindows];
        }

        if (OperatingSystem.IsMacOS() && MacOsWkWebViewHost.IsMainThread
            && windows.Any(static window => window.RequiresMainThreadEventPump))
        {
            while (!task.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var window in windows)
                {
                    window.ProcessEmbeddedHostEvents();
                }
                Thread.Sleep(10);
                lock (Gate)
                {
                    windows = [.. RunningWindows];
                }
            }
            return task;
        }

        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    /// <summary>Requests all running Runic Desktop windows to close.</summary>
    public static void Exit() => ExitAsync().GetAwaiter().GetResult();

    /// <summary>Asynchronously requests all running Runic Desktop windows to close.</summary>
    public static Task ExitAsync(CancellationToken cancellationToken = default)
    {
        WebUiWindow[] windows;
        lock (Gate)
        {
            windows = [.. RunningWindows];
        }

        return Task.WhenAll(windows.Select(window => window.CloseFromApplicationAsync(cancellationToken)));
    }

    internal static bool MultiClient => Volatile.Read(ref _multiClient) != 0;

    internal static bool UseCookies => Volatile.Read(ref _useCookies) != 0;

    internal static bool ShowWaitConnection => Volatile.Read(ref _showWaitConnection) != 0;

    internal static TimeSpan ConnectionTimeout
    {
        get
        {
            var configured = TimeSpan.FromTicks(Volatile.Read(ref _connectionTimeoutTicks));
            return configured == TimeSpan.Zero ? TimeSpan.FromSeconds(15) : configured;
        }
    }

    internal static string? GetBrowserFolder()
    {
        lock (Gate)
        {
            return _browserFolder;
        }
    }

    internal static IWebUiEmbeddedHost CreateEmbeddedHost()
    {
        IWebUiEmbeddedHostFactory factory;
        lock (Gate)
        {
            factory = _embeddedHostFactory;
        }
        if (!factory.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "No embedded WebView host is available. Install the platform runtime or configure a custom host factory.");
        }
        return factory.Create();
    }

    internal static string GetDefaultRootFolder()
    {
        lock (Gate)
        {
            return _defaultRootFolder;
        }
    }

    internal static void Started(WebUiWindow window)
    {
        lock (Gate)
        {
            if (RunningWindows.Count == 0)
            {
                _allClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            RunningWindows.Add(window);
        }
    }

    internal static void Stopped(WebUiWindow window)
    {
        lock (Gate)
        {
            if (RunningWindows.Remove(window) && RunningWindows.Count == 0)
            {
                _allClosed.TrySetResult();
            }
        }
    }

    internal static void RegisterGeneratedProfile(string path)
    {
        lock (Gate)
        {
            GeneratedProfiles.Add(path);
        }
    }

    internal static bool TryDeleteGeneratedProfile(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            lock (Gate)
            {
                GeneratedProfiles.Remove(path);
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
}
