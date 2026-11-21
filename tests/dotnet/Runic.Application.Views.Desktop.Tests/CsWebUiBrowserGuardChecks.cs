extern alias cswebui;
using System.Diagnostics;
using TUnit.Core;
using CsWebUiBrowserGuard = cswebui::Runic.Application.Views.CsWebUi.CsWebUiBrowserGuard;
using CsWebUiRemoteDebugging = cswebui::Runic.Application.Views.CsWebUi.CsWebUiRemoteDebugging;
using Session = cswebui::Runic.Application.Views.CsWebUi.CsWebUiRemoteDebugging.Session;

// sdk#1263: the browser of a Development CS-WebUI window's per-run profile ends with the application, also when the
// application is killed, and a profile such an end left is swept by the next run. The watcher (Linux, macOS) runs here
// against stand-in browser processes; the probes' --probe-browser-lifetime kills a real application with a real Chromium
// or Edge, which also covers the Windows job object.
internal sealed class CsWebUiBrowserGuardChecks : IDisposable
{
    private readonly List<string> _directories = [];
    private readonly List<Process> _processes = [];

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            TestProcesses.StopTreeAsync(process).GetAwaiter().GetResult();
            process.Dispose();
        }
        foreach (var directory in _directories) TestProcesses.DeleteDirectory(directory);
    }

    private static string Directory_(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private string Root()
    {
        var root = Directory.CreateTempSubdirectory("runic-guard-checks-").FullName;
        _directories.Add(root);
        return root;
    }

    // A process whose command line names the profile as Chromium's does, until it is ended.
    private Process StandIn(string profile)
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var argument in new[] { "-c", "while :; do sleep 1; done", "stand-in-browser", $"--user-data-dir={profile}" })
            start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The stand-in did not start.");
        _processes.Add(process);
        return process;
    }

    [Test]
    public async Task AWatcherEndsItsProfilesBrowserAndDeletesTheProfileWhenTheApplicationEnds()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Skip.Test("Windows holds the browser in a job object instead.");
        var root = Root();
        var profile = Directory_(root, CsWebUiRemoteDebugging.ProfilePrefix + "watched");
        var browser = StandIn(profile);
        // The user's browser: its profile's path starts like this run's, but is another.
        var users = StandIn(profile + "-user");
        await Task.Delay(300);
        var watcher = CsWebUiBrowserGuard.WatchProfile(profile) ?? throw new InvalidOperationException("No watcher started.");
        await Task.Delay(500);
        Require(!browser.HasExited, "The browser ended while the application ran.");
        watcher.Abandon();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            await browser.WaitForExitAsync(timeout.Token);
        for (var attempt = 0; attempt < 100 && Directory.Exists(profile); attempt++) await Task.Delay(100);
        Require(!Directory.Exists(profile), "The profile was not deleted.");
        Require(!users.HasExited, "A browser of another profile ended too.");
    }

    [Test]
    public async Task AReleasedWatcherLeavesTheBrowserAndTheProfile()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Skip.Test("Windows holds the browser in a job object instead.");
        var root = Root();
        var profile = Directory_(root, CsWebUiRemoteDebugging.ProfilePrefix + "released");
        var browser = StandIn(profile);
        var watcher = CsWebUiBrowserGuard.WatchProfile(profile) ?? throw new InvalidOperationException("No watcher started.");
        watcher.Dispose();
        await Task.Delay(1500);
        Require(!browser.HasExited, "A released watcher ended the browser.");
        Require(Directory.Exists(profile), "A released watcher deleted the profile.");
    }

    [Test]
    public void TheSweepDeletesOnlyProfilesNoRunUses()
    {
        var root = Root();
        var now = DateTime.UtcNow;
        // Left by a killed run: its owner file is free.
        var left = Directory_(root, CsWebUiRemoteDebugging.ProfilePrefix + "left");
        File.WriteAllText(Path.Combine(left, CsWebUiRemoteDebugging.OwnerFile), "");
        // A running application's: it holds its owner file.
        var held = Directory_(root, CsWebUiRemoteDebugging.ProfilePrefix + "held");
        using var owner = new FileStream(Path.Combine(held, CsWebUiRemoteDebugging.OwnerFile), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        // Without an owner file (0.23, or a run between creating the directory and the file): kept while new.
        var fresh = Directory_(root, CsWebUiRemoteDebugging.ProfilePrefix + "fresh");
        // The current profile, and a directory of another name.
        var current = Directory_(root, CsWebUiRemoteDebugging.ProfilePrefix + "current");
        var other = Directory_(root, "runic-other-left");

        var deleted = Session.SweepStaleProfiles(root, current, now);
        Require(deleted.SequenceEqual([left]), $"Deleted: {string.Join(", ", deleted)}");
        Require(Directory.Exists(held) && Directory.Exists(fresh) && Directory.Exists(current) && Directory.Exists(other), "A profile in use was deleted.");

        // A day later, the profile without an owner file is stale, unless its browser runs.
        deleted = Session.SweepStaleProfiles(root, current, now + TimeSpan.FromDays(2));
        Require(deleted.SequenceEqual([fresh]), $"A day later, deleted: {string.Join(", ", deleted)}");
        Require(Directory.Exists(held), "A held profile was deleted a day later.");
    }

    [Test]
    public void TheSweepLeavesAProfileWhoseBrowserRuns()
    {
        var root = Root();
        var running = Directory_(root, CsWebUiRemoteDebugging.ProfilePrefix + "running");
        File.WriteAllText(Path.Combine(running, CsWebUiRemoteDebugging.OwnerFile), "");
        using var self = Process.GetCurrentProcess();
        FileStream? chromiumLock = null;
        if (OperatingSystem.IsWindows())
            chromiumLock = new FileStream(Path.Combine(running, "lockfile"), FileMode.Create, FileAccess.Write, FileShare.Read);
        else
            File.CreateSymbolicLink(Path.Combine(running, "SingletonLock"), $"{System.Net.Dns.GetHostName()}-{self.Id}");
        using (chromiumLock)
            Require(Session.SweepStaleProfiles(root, current: null, DateTime.UtcNow).Count == 0, "A profile whose browser runs was deleted.");
        if (OperatingSystem.IsWindows()) return;
        // A browser that ended: its lock names a process that is gone.
        var ended = Directory_(root, CsWebUiRemoteDebugging.ProfilePrefix + "ended");
        File.WriteAllText(Path.Combine(ended, CsWebUiRemoteDebugging.OwnerFile), "");
        File.CreateSymbolicLink(Path.Combine(ended, "SingletonLock"), $"{System.Net.Dns.GetHostName()}-{int.MaxValue - 7}");
        var deleted = Session.SweepStaleProfiles(root, current: null, DateTime.UtcNow);
        Require(deleted.SequenceEqual([ended]), $"Deleted: {string.Join(", ", deleted)}");
    }

    [Test]
    public void ASessionHoldsItsProfileUntilItIsReleased()
    {
        using var session = new Session { Guards = true };
        string profile;
        lock (session.Gate) profile = session.Profile = session.CreateProfile();
        _directories.Add(profile);
        Require(File.Exists(Path.Combine(profile, CsWebUiRemoteDebugging.OwnerFile)), "The profile has no owner file.");
        var root = Path.GetDirectoryName(profile)!;
        Require(!Session.SweepStaleProfiles(root, current: null, DateTime.UtcNow).Contains(profile), "Another run's sweep deleted a profile in use.");
        session.Dispose();
        Require(Session.SweepStaleProfiles(root, current: null, DateTime.UtcNow).Contains(profile), "A released profile was not swept.");
    }

    [Test]
    public void TheJobTakesOnlyABrowserThatStartedWithTheWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("The job object is Windows'.");
            return;
        }
        var job = new CsWebUiBrowserGuard.Job();
        using var self = Process.GetCurrentProcess();
        // This process started long before a window shown now.
        Require(!job.Hold((nuint)self.Id, DateTime.UtcNow + TimeSpan.FromMinutes(1)), "A process that started before the window was held.");
        Require(!job.Hold(0, DateTime.UtcNow), "No process was held.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
