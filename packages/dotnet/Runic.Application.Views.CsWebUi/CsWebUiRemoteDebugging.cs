using System.Globalization;
using CsWebUi;
using Runic.Desktop.Internal;

namespace Runic.Application.Views.CsWebUi;

// sdk#1221 (D070.7, D070.17): the Chrome DevTools Protocol endpoint of a CS-WebUI browser window's Chromium or Edge, which
// dotnet runic dev --debug-endpoint, --no-window and the template's "CS-WebUI script debugging" profile ask for with
// RUNIC_WEBUI_REMOTE_DEBUGGING_PORT. Runic Desktop's installed-browser windows have had it since 0.22
// (Runic.Desktop WebUiRemoteDebugging, linked here); this is the same contract for the windows WebUI's own browser
// launcher starts. It opens only when all of these hold:
// - Development, a valid RUNIC_WEBUI_REMOTE_DEBUGGING_PORT, and an untrimmed build (WebUiRemoteDebugging.RequestedPort,
//   IsSupported);
// - a browser window that this host shows: not UseWebView, ShowWindow or serve-only mode;
// - a Chromium-based browser: CSWEBUI_BROWSER_PATH's, or WebUI's best one.
// The browser then runs with a profile of this run's own (DEV-SEC-85): a new 0700 temporary directory, set with SetProfile
// before ConfigureWindow, instead of WebUI's .WebUI/WebUI<Browser>Profile, which every WebUI application of the user shares
// and which persists. The process's windows share it, and so one browser: the first window's browser opens the endpoint
// (the opener), and a window shown while that browser runs joins it, asks for no port and reports nothing, since the
// opener's endpoint reaches its page too. Once that browser exits, its profile is deleted, and the next window opens a new
// one. The host confirms the endpoint before it reports it (DEV-SEC-84):
// - port 0: Chromium writes the port it bound into DevToolsActivePort in that profile, which only the user can write;
// - a fixed port: Chromium writes no record, so beyond WebUiRemoteDebugging's checks the browser process that WebUI
//   started must hold the listener on 127.0.0.1 (CsWebUiListenerOwner).
// sdk#1263: that browser also ends with the application when the application is ended without a close of its own, such as
// a debugger's Stop (CsWebUiBrowserGuard), and a profile such an end left behind is deleted by the next run.
// WebUI's custom parameters replace its Chromium defaults, so the parameters repeat them (WebUiChromiumDefaults) before the
// port.
internal sealed class CsWebUiRemoteDebugging : IDisposable
{
    // The switches WebUI 24c7d196 passes a Chromium window without custom parameters (webui.c, _webui_get_browser_args),
    // with --no-proxy-server, which it passes without a proxy.
    internal static readonly string[] WebUiChromiumDefaults =
    [
        "--no-first-run", "--safe-mode", "--disable-extensions", "--disable-background-mode", "--disable-plugins",
        "--disable-plugins-discovery", "--disable-translate", "--disable-features=Translate", "--bwsi", "--disable-sync",
        "--disable-sync-preferences", "--disable-component-update", "--allow-insecure-localhost",
        "--auto-accept-camera-and-microphone-capture", "--no-proxy-server",
    ];

    // The profile name WebUI requires beside the path; Chromium uses only the path.
    internal const string ProfileName = "RunicDevelopment";

    internal const string ProfilePrefix = "runic-cswebui-devtools-";

    // sdk#1263: the file a run holds open in its profile while the profile is in use (Session.SweepStaleProfiles).
    internal const string OwnerFile = "runic-owner.lock";

    private static readonly string[] ChromiumNames = ["chrome", "chromium", "msedge", "edge", "brave", "vivaldi", "yandex", "epic"];

    private readonly Session _session;
    private WebUiRemoteDebugging.Reservation? _reservation;
    private DateTime _shownAfter;
    private bool _pending = true;

    private CsWebUiRemoteDebugging(Session session, string profile, bool opens, int port, int requested,
        WebUiRemoteDebugging.Reservation? reservation)
    {
        _session = session;
        Profile = profile;
        Opens = opens;
        Port = port;
        Requested = requested;
        _reservation = reservation;
    }

    // The per-run profile directory the window's browser uses.
    internal string Profile { get; }

    // Whether this window's browser opens the endpoint; false when it joins the browser of an earlier window.
    internal bool Opens { get; }

    // The port Chromium is asked for (0 for any; the record names the one it bound), and the one the variable asked for.
    internal int Port { get; }

    internal int Requested { get; }

    // The browser's command-line parameters for the window: a joining window's browser is the opener's, which ignores them,
    // but they apply if that browser exited meanwhile, and then open no second endpoint.
    internal string Parameters => Opens
        ? string.Join(' ', [.. WebUiChromiumDefaults, WebUiRemoteDebugging.Argument(Port)])
        : string.Join(' ', WebUiChromiumDefaults);

    // Whether the browser WebUI starts is Chromium-based: the one CSWEBUI_BROWSER_PATH names, else WebUI's best one.
    internal static bool IsChromiumBased(string? configuredPath, WebUiBrowser best)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var name = Path.GetFileNameWithoutExtension(configuredPath.TrimEnd('/', '\\')).ToLowerInvariant();
            return ChromiumNames.Any(name.Contains);
        }
        return best is WebUiBrowser.Chrome or WebUiBrowser.Edge or WebUiBrowser.Chromium or WebUiBrowser.Brave
            or WebUiBrowser.Vivaldi or WebUiBrowser.Epic or WebUiBrowser.Yandex;
    }

    // The endpoint a browser window asks for, with a fixed port reserved until ReleasePort, or null for none. A
    // non-Chromium browser writes a warning instead.
    internal static CsWebUiRemoteDebugging? Request(Func<string, string?> readEnvironment, bool browserWindow, Func<bool> chromiumBased,
        Action<string> warn, Session session)
    {
        if (!WebUiRemoteDebugging.IsSupported || !browserWindow) return null;
        if (WebUiRemoteDebugging.RequestedPort(readEnvironment, devTools: null) is not int requested) return null;
        if (!chromiumBased())
        {
            warn($"warning: Runic: {WebUiRemoteDebugging.PortVariable} asks for a DevTools Protocol endpoint, but the CS-WebUI window's browser is not Chromium-based, so it opens none.");
            return null;
        }
        lock (session.Gate)
        {
            var profile = session.Profile ??= session.CreateProfile();
            if (session.Opener is not null)
            {
                session.Joining++;
                return new CsWebUiRemoteDebugging(session, profile, opens: false, port: 0, requested, reservation: null);
            }
            // A record that an earlier browser of this profile left would name a port nobody listens on.
            WebUiRemoteDebugging.DeleteStaleRecord(profile);
            var reservation = requested == 0 ? null : WebUiRemoteDebugging.Reserve(requested);
            if (reservation is { FellBack: true })
            {
                warn($"warning: Runic: port {requested} for the browser's DevTools Protocol endpoint is taken on 127.0.0.1 or [::1]; the browser uses a free port instead, which the {WebUiRemoteDebugging.OpenedPrefix.TrimEnd('=')} line names. Do not attach a debugger to port {requested}.");
                reservation = null;
            }
            var opener = new CsWebUiRemoteDebugging(session, profile, opens: true, reservation?.Port ?? 0, requested, reservation);
            session.Opener = opener;
            return opener;
        }
    }

    // Just before the browser starts.
    internal void ReleasePort()
    {
        _reservation?.Dispose();
        _reservation = null;
        _shownAfter = DateTime.UtcNow;
    }

    // Without Start, the window was not shown: an opener gives way to the next window.
    public void Dispose()
    {
        ReleasePort();
        Started(processId: null);
    }

    // The endpoint of a window the host is about to show, with the window's profile and parameters set, or null for none.
    internal static CsWebUiRemoteDebugging? ForWindow(WebUiWindow window, bool browserWindow)
    {
        // First, so a trimmed build, where the switch is off, keeps none of the code below or its delegates.
        if (!WebUiRemoteDebugging.IsSupported) return null;
        var debugging = Request(Environment.GetEnvironmentVariable, browserWindow,
            () => IsChromiumBased(Environment.GetEnvironmentVariable(WebUiApplication.BrowserPathEnvironmentVariable), window.BestBrowser),
            WebUiRemoteDebugging.WriteWarning, Session.Process);
        if (debugging is null) return null;
        try
        {
            window.SetProfile(ProfileName, debugging.Profile);
            window.SetCustomParameters(debugging.Parameters);
        }
        catch
        {
            debugging.Dispose();
            throw;
        }
        return debugging;
    }

    // Once the window is shown: an opener reports the endpoint in the background, until its browser exits.
    internal void Start(WebUiWindow window)
    {
        nuint processId;
        try { processId = window.BrowserProcessId; }
        catch (ObjectDisposedException) { processId = 0; }
        if (Started(processId) is not { } exited) return;
        _ = ReportAsync(exited, processId, WebUiRemoteDebugging.WriteLine, WebUiRemoteDebugging.WriteWarning);
    }

    // Ends the window's pending state. For an opener with a browser process, returns that browser's exit, after which the
    // session forgets the opener and deletes the profile. For an opener whose browser WebUI did not find, a completed task:
    // the session forgets it, so the next window opens anew. Null for a joining window, a second call, or a window that
    // was not shown.
    internal Task? Started(nuint? processId)
    {
        lock (_session.Gate)
        {
            if (!_pending) return null;
            _pending = false;
            if (!Opens)
            {
                _session.Joining--;
                return null;
            }
            if (processId is not { } pid || pid == 0)
            {
                // The browser may run all the same, so its profile stays for the next opener.
                _session.Forget(this, browserExited: false);
                return processId is null ? null : Task.CompletedTask;
            }
        }
        _session.HoldBrowser(processId.Value, _shownAfter);
        var exited = _session.BrowserExited(processId.Value);
        _ = exited.ContinueWith(static (_, state) =>
        {
            var self = (CsWebUiRemoteDebugging)state!;
            lock (self._session.Gate) self._session.Forget(self, browserExited: true);
        }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return exited;
    }

    // After the browser started: confirms the endpoint, writes RUNIC_APPLICATION_DEBUG_ENDPOINT (standard output), and its
    // end when the browser exits; or a warning on standard error when it cannot be confirmed.
    internal Task ReportAsync(Task exited, nuint processId, Action<string> writeLine, Action<string> warn,
        WebUiRemoteDebugging.Probes? probes = null, TimeProvider? time = null)
    {
        if (processId == 0)
        {
            warn("warning: Runic: the CS-WebUI window's DevTools Protocol endpoint is not reported: WebUI found no browser process for the window.");
            return Task.CompletedTask;
        }
        var shownAfter = _shownAfter;
        probes ??= WebUiRemoteDebugging.Probes.System with
        {
            ForeignListener = port => CsWebUiListenerOwner.ForeignListener(processId, shownAfter, port),
        };
        return WebUiRemoteDebugging.ReportAsync(exited, Profile, Port, writeLine, (port, reason) =>
            warn(port == 0
                ? $"warning: Runic: the CS-WebUI window's browser opened no DevTools Protocol endpoint: {reason}."
                : $"warning: Runic: the CS-WebUI window's browser opened no DevTools Protocol endpoint on 127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}: {reason}. Do not attach a debugger to that port."),
            time, probes);
    }

    // The process's per-run profile and the window whose browser holds the endpoint.
    // The process's session is never disposed: its owner file and watcher end with the process.
    internal sealed class Session : IDisposable
    {
        private static Session? s_process;

        internal static Session Process
        {
            get
            {
                if (s_process is { } existing) return existing;
                var created = new Session { Guards = true };
                return Interlocked.CompareExchange(ref s_process, created, null) ?? created;
            }
        }

        internal readonly Lock Gate = new();

        // sdk#1263: whether the session's profiles and browsers are guarded (CsWebUiBrowserGuard) and a run's stale ones are
        // swept: the process's session; tests opt in.
        internal bool Guards { get; init; }

        // The current profile's owner file, held open while the profile is in use, and its watcher.
        private FileStream? _owner;
        private CsWebUiBrowserGuard.Watcher? _watcher;
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private CsWebUiBrowserGuard.Job? _job;
        private bool _swept;

        // The directory, created for the first window that asks and deleted once its browser exits.
        internal string? Profile;

        // The window whose browser opens the endpoint, from its Request until that browser exits or it was not shown.
        internal CsWebUiRemoteDebugging? Opener;

        // Windows that joined the opener and are not shown yet: the profile stays while one may still start a browser.
        internal int Joining;

        // Completes when the browser process exits; replaceable in tests.
        internal Func<nuint, Task> BrowserExited { get; init; } = CsWebUiRemoteDebugging.BrowserExited;

        // Under Gate. The profile is deleted only once its browser exited and no joining window can start another.
        internal void Forget(CsWebUiRemoteDebugging opener, bool browserExited)
        {
            if (!ReferenceEquals(Opener, opener)) return;
            Opener = null;
            if (!browserExited || Joining != 0 || Profile is not { } profile) return;
            Profile = null;
            ReleaseProfile();
            _ = DeleteProfileAsync(profile);
        }

        // Under Gate: a new 0700 profile directory, with its owner file and, on Linux and macOS, its watcher; the first
        // also starts the sweep of the profiles earlier runs left.
        internal string CreateProfile()
        {
            var profile = Directory.CreateTempSubdirectory(ProfilePrefix).FullName;
            if (!Guards) return profile;
            try
            {
                _owner = new FileStream(Path.Combine(profile, OwnerFile), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Without it, a later run's sweep leaves the profile alone until it is a day old.
            }
            _watcher = CsWebUiBrowserGuard.WatchProfile(profile);
            if (!_swept)
            {
                _swept = true;
                var root = Path.GetTempPath();
                _ = Task.Run(() => SweepStaleProfiles(root, profile, DateTime.UtcNow));
            }
            return profile;
        }

        // Releases the profile's owner file and watcher without deleting the profile.
        public void Dispose()
        {
            lock (Gate) ReleaseProfile();
        }

        // Under Gate: the profile is about to be deleted, so its owner file closes and its watcher is released.
        private void ReleaseProfile()
        {
            _owner?.Dispose();
            _owner = null;
            _watcher?.Dispose();
            _watcher = null;
        }

        // Windows: puts an opener's browser in the session's kill-on-close job (CsWebUiBrowserGuard.Job).
        internal void HoldBrowser(nuint processId, DateTime shownAfter)
        {
            if (!Guards || !OperatingSystem.IsWindows()) return;
            CsWebUiBrowserGuard.Job job;
            lock (Gate) job = _job ??= new CsWebUiBrowserGuard.Job();
            _ = job.Hold(processId, shownAfter);
        }

        // Deletes the runic-cswebui-devtools-* profiles in root that no run uses any more: an application that was ended
        // without its own close leaves its profile (sdk#1263). A profile is in use while its owner file is held open
        // (FileShare.None: a share mode on Windows, flock elsewhere) or its browser runs (Chromium's lock: lockfile, held
        // open on Windows; SingletonLock, a link to <host>-<pid>, elsewhere). A profile without an owner file, as 0.23
        // made, or whose owner file cannot be read, counts only when its browser is gone and it is a day old. Only
        // directories that are not links and that no other user can enter are considered. Returns those deleted.
        internal static List<string> SweepStaleProfiles(string root, string? current, DateTime now)
        {
            var deleted = new List<string>();
            IEnumerable<DirectoryInfo> candidates;
            try { candidates = new DirectoryInfo(root).EnumerateDirectories(ProfilePrefix + "*").ToList(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return deleted;
            }
            foreach (var directory in candidates)
            {
                try
                {
                    if (string.Equals(directory.FullName, current, StringComparison.Ordinal) || directory.LinkTarget is not null) continue;
                    if (!OperatingSystem.IsWindows() &&
                        (directory.UnixFileMode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                    {
                        continue;
                    }
                    FileStream? owner = null;
                    try
                    {
                        var ownerPath = Path.Combine(directory.FullName, OwnerFile);
                        if (File.Exists(ownerPath))
                        {
                            // Held by its run: in use. Opened here, it also keeps another run's sweep away meanwhile.
                            try { owner = new FileStream(ownerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                            catch (IOException) { continue; }
                        }
                        if (owner is null && now - directory.CreationTimeUtc < TimeSpan.FromDays(1)) continue;
                        if (BrowserRuns(directory.FullName)) continue;
                        owner?.Dispose();
                        owner = null;
                        directory.Delete(recursive: true);
                        deleted.Add(directory.FullName);
                    }
                    finally
                    {
                        owner?.Dispose();
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // In use after all, or another run deleted it meanwhile.
                }
            }
            return deleted;
        }

        // Whether a Chromium browser holds the profile's lock.
        internal static bool BrowserRuns(string profile)
        {
            if (OperatingSystem.IsWindows())
            {
                var lockFile = Path.Combine(profile, "lockfile");
                if (!File.Exists(lockFile)) return false;
                try
                {
                    using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    return false;
                }
                catch (IOException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
            }
            string? target;
            try { target = new FileInfo(Path.Combine(profile, "SingletonLock")).LinkTarget; }
            catch (IOException) { return true; }
            if (target is null) return File.Exists(Path.Combine(profile, "SingletonLock"));
            // <host>-<pid>; a browser of another host (a shared temporary directory) counts as running.
            var dash = target.LastIndexOf('-');
            if (dash <= 0 || !int.TryParse(target.AsSpan(dash + 1), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var pid)) return true;
            if (!string.Equals(target[..dash], System.Net.Dns.GetHostName(), StringComparison.Ordinal)) return true;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }

        // The browser's helper processes can hold files for a moment after it exits.
        internal static async Task DeleteProfileAsync(string profile)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
                    return;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                }
            }
        }
    }

    // Completes when the browser process exits, or at once when it is gone already.
    internal static Task BrowserExited(nuint processId)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(checked((int)processId));
            return process.WaitForExitAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            return Task.CompletedTask;
        }
    }
}
