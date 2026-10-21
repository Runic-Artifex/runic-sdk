using Microsoft.Win32;
using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// IDesktopFileLauncher.LaunchUriAsync on Windows (W319-003 desktop-05, plat-16). A test-owned URL protocol in
// HKEY_CURRENT_USER\Software\Classes whose handler is this test executable (--record-launch) proves that the launcher
// refuses that scheme and paths without starting anything, and that its ShellExecuteEx dispatch reaches the scheme's
// handler: the same native call an https URI takes, without opening a browser on the runner. CI only.
#pragma warning disable CA1416 // The case checks for Windows first.
internal sealed class UriLauncherTests
{
    [Test, Explicit, Category("NativeApplications"), DisplayName("URI launcher: paths and other schemes are refused, ShellExecuteEx reaches the URL handler and Settings")]
    public async Task ShellUrlAssociationAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) throw new PlatformNotSupportedException("The shell's URL associations require Windows.");
        string unique = Guid.NewGuid().ToString("N")[..8];
        string scheme = "runic-uri-" + unique;
        string root = Path.Combine(Path.GetTempPath(), "runic-uri-" + unique);
        string log = Path.Combine(root, "launches.log");
        // The apphost, also when the tests run through dotnet; the NativeAOT executable is the same file name.
        string probe = Path.Combine(AppContext.BaseDirectory, "Runic.Platform.Windows.Tests.exe");
        Directory.CreateDirectory(root);
        var owner = new StaOwner();
        try
        {
            Check(File.Exists(probe), $"the probe executable exists: {probe}");
            using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
            {
                using (var key = classes.CreateSubKey(scheme)) { key.SetValue("", "URL:Runic URI test " + unique); key.SetValue("URL Protocol", ""); }
                using (var key = classes.CreateSubKey(scheme + @"\shell\open\command")) key.SetValue("", $"\"{probe}\" --record-launch \"{log}\" \"%1\"");
            }
            AssociationsChanged();

            // The public launcher refuses the registered test scheme, the probe's own path and a UNC path: none reaches the
            // owner's thread or the shell.
            var launcher = WindowsPlatformProvider.CreateFileLauncher(owner);
            foreach (var uri in new[] { new Uri(scheme + ":refused"), new Uri(probe), new Uri(@"\\localhost\C$\Windows\System32\cmd.exe"), new Uri("file:///" + probe.Replace('\\', '/')) })
            {
                try { await launcher.LaunchUriAsync(uri); throw new InvalidOperationException($"LaunchUriAsync accepted {uri}"); }
                catch (ArgumentException) { }
            }
            Check(owner.Dispatches == 0, "a refused URI reached the owner's thread");

            // The dispatch behind an accepted URI, with the test scheme.
            string target = scheme + ":open/a%20b?c=d";
            var dispatched = await new WindowsUriLauncher(owner).DispatchAsync(target, CancellationToken.None);
            Console.WriteLine($"ShellExecuteEx {target}: {dispatched}");
            Check(dispatched is PlatformResult<PlatformUnit>.Success && owner.Dispatches == 1, "the shell accepted the test URI");
            var lines = await RecordedAsync(log, 1);
            Console.WriteLine("Recorded:\n" + string.Join('\n', lines));
            Check(lines.Any(line => line.StartsWith(scheme + ":open/a", StringComparison.Ordinal)), "the URI reached its protocol handler");

            // A scheme without a handler: Windows 11 reports success and offers to find an application (OpenWith), even
            // without UI requested, so the outcome is recorded rather than asserted; the chooser is closed.
            var unregistered = await new WindowsUriLauncher(owner).DispatchAsync("runic-unregistered-" + unique + ":x", CancellationToken.None);
            await Task.Delay(2000);
            Console.WriteLine($"Unregistered scheme: {unregistered}; {ApplicationsTests.Choosers()} Open With choosers closed");
            Check(File.ReadAllLines(log).Length == lines.Count, "nothing else reached the probe");

            // Windows Settings (D028.5, D031.6): the public launcher hands ms-settings:display to the shell, which opens the
            // Settings app. Values outside the page rule (a path, a long page, a query, a fragment, an -action page) are
            // refused before the owner's thread.
            int before = owner.Dispatches;
            foreach (var text in new[] { "ms-settings:../../Windows/System32/cmd.exe", "ms-settings:" + new string('a', 4000), "ms-settings:display?x=1",
                "ms-settings:display#top", "ms-settings:windowsupdate-action" })
            {
                try { await launcher.LaunchUriAsync(new Uri(text)); throw new InvalidOperationException($"LaunchUriAsync accepted {text[..Math.Min(text.Length, 60)]}"); }
                catch (ArgumentException) { }
            }
            Check(owner.Dispatches == before, "a refused Settings URI reached the owner's thread");
            var settings = await launcher.LaunchUriAsync(new Uri("ms-settings:display"));
            Console.WriteLine($"LaunchUriAsync ms-settings:display: {settings}");
            Check(settings is PlatformResult<PlatformUnit>.Success && owner.Dispatches == before + 1, "the shell opened ms-settings:display");
            await Task.Delay(3000);
            int closed = CloseSettings();
            Console.WriteLine($"{closed} Settings processes closed");
            Console.WriteLine("PASS Windows URI launcher: refusals before dispatch, ShellExecuteEx through the URL association.");
        }
        finally
        {
            using (var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true))
                classes?.DeleteSubKeyTree(scheme, throwOnMissingSubKey: false);
            AssociationsChanged();
            for (int attempt = 0; attempt < 10 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { await Task.Delay(500); } // The probe may still hold the log.
            }
        }
    }

    // Closes the Settings app the ms-settings: launches opened (SystemSettings.exe on Windows 10 and 11, and Server).
    private static int CloseSettings()
    {
        int closed = 0;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("SystemSettings"))
        {
            using (process)
            {
                try { process.Kill(); closed++; } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return closed;
    }

    // Runs each dispatch on its own STA thread without a window, as a window's thread would.
    private sealed class StaOwner : INativeWindowOwner
    {
        internal int Dispatches;
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => true;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
        {
            Dispatches++;
            Exception? failure = null;
            var thread = new Thread(() => { try { action(0); } catch (Exception error) { failure = error; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
        }
    }

    private static async Task<List<string>> RecordedAsync(string log, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        List<string> lines = [];
        while (DateTime.UtcNow < deadline)
        {
            try { lines = File.Exists(log) ? [.. File.ReadAllLines(log)] : []; } catch (IOException) { }
            if (lines.Count >= count) break;
            await Task.Delay(250);
        }
        return lines;
    }

    private static void AssociationsChanged() => ApplicationsTests.AssociationsChanged();

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("URI launcher check failed: " + message);
    }
}
