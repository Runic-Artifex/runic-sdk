namespace CsWebUi.Managed;

/// <summary>Provides process-wide configuration and lifetime operations for managed WebUI windows.</summary>
public static class WebUiApplication
{
    private static readonly object Gate = new();
    private static readonly HashSet<WebUiWindow> RunningWindows = [];
    private static TaskCompletionSource _allClosed = CompletedSource();
    private static string _defaultRootFolder = Path.GetFullPath(Environment.CurrentDirectory);
    private static int _multiClient;
    private static int _useCookies = 1;

    /// <summary>Gets whether at least one managed WebUI server is running.</summary>
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

    /// <summary>Sets a process-wide managed WebUI configuration option.</summary>
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
            case WebUiConfiguration.UiEventBlocking:
            case WebUiConfiguration.FolderMonitor:
            case WebUiConfiguration.AsynchronousResponse:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(configuration));
        }
    }

    /// <summary>Sets the root folder copied by subsequently created managed windows.</summary>
    public static void SetDefaultRootFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"The managed WebUI root folder does not exist: {fullPath}");
        }

        lock (Gate)
        {
            _defaultRootFolder = fullPath;
        }
    }

    /// <summary>Blocks until all running managed WebUI windows have closed.</summary>
    public static void Wait() => WaitAsync().GetAwaiter().GetResult();

    /// <summary>Asynchronously waits until all running managed WebUI windows have closed.</summary>
    public static Task WaitAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (Gate)
        {
            task = _allClosed.Task;
        }

        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    /// <summary>Requests all running managed WebUI windows to close.</summary>
    public static void Exit() => ExitAsync().GetAwaiter().GetResult();

    /// <summary>Asynchronously requests all running managed WebUI windows to close.</summary>
    public static Task ExitAsync(CancellationToken cancellationToken = default)
    {
        WebUiWindow[] windows;
        lock (Gate)
        {
            windows = [.. RunningWindows];
        }

        return Task.WhenAll(windows.Select(window => window.CloseAsync(cancellationToken)));
    }

    internal static bool MultiClient => Volatile.Read(ref _multiClient) != 0;

    internal static bool UseCookies => Volatile.Read(ref _useCookies) != 0;

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

    private static TaskCompletionSource CompletedSource()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
}
