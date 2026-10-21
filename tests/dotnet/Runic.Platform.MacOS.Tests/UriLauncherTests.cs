using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

// IDesktopFileLauncher.LaunchUriAsync on macOS (W319-003 desktop-05, plat-16). A test-owned application bundle that
// declares a URL scheme unique to the run proves that the launcher refuses that scheme and paths without dispatching,
// and that its NSWorkspace dispatch reaches the scheme's application: the same call an https URI takes, without opening
// a browser on the runner. The bundle is registered with Launch Services, and unregistered and deleted afterwards. CI only.
internal sealed class UriLauncherTests
{
    [Test, Explicit, Category("NativeApplications"), DisplayName("URI launcher: paths and other schemes are refused, NSWorkspace reaches the URL handler")]
    public async Task WorkspaceUrlHandlerAsync()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("NSWorkspace requires macOS.");
        string unique = Guid.NewGuid().ToString("N")[..8];
        string scheme = "runic-uri-" + unique, id = "eu.runic.tests.uri-" + unique;
        string root = Path.Combine(Path.GetTempPath(), "runic-uri-" + unique);
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "runic-uri-tests-" + unique);
        string marker = Path.Combine(root, "starts.log");
        Directory.CreateDirectory(root);
        string? bundle = null;
        try
        {
            Directory.CreateDirectory(installed);
            bundle = ApplicationsTests.CreateBundle(installed, "Runic URI " + unique, id, "eu.runic.tests.uri-document-" + unique, "runicuri" + unique,
                "application/x-runic-uri-" + unique, scheme, marker, exports: true);
            Console.WriteLine($"LSRegisterURL {bundle}: {ApplicationsTests.Register(bundle)}; lsregister -f: {ApplicationsTests.RunRegister("-f", bundle)}");
            var applications = MacOSPlatformProvider.CreateApplications();
            bool registered = false;
            for (int attempt = 0; attempt < 20 && !registered; attempt++)
            {
                registered = (await applications.GetApplicationsForAsync("x-scheme-handler/" + scheme)) is PlatformResult<System.Collections.Immutable.ImmutableArray<DesktopApplicationInfo>>.Success(var handlers)
                    && handlers.Any(app => app.Id == id);
                if (!registered) await Task.Delay(500);
            }
            Check(registered, "Launch Services knows the test scheme's application");

            // The public launcher refuses the registered test scheme and paths: none reaches the owner's thread.
            var owner = new CountingOwner();
            var launcher = MacOSPlatformProvider.CreateFileLauncher(owner);
            foreach (var uri in new[] { new Uri(scheme + ":refused"), new Uri(bundle), new Uri("file://" + bundle), new Uri("/bin/sh") })
            {
                try { await launcher.LaunchUriAsync(uri); throw new InvalidOperationException($"LaunchUriAsync accepted {uri}"); }
                catch (ArgumentException) { }
            }
            Check(owner.Dispatches == 0, "a refused URI reached the owner's thread");
            // A Windows Settings URI is accepted and unavailable on macOS, without reaching the owner's thread.
            Check(await launcher.LaunchUriAsync(new Uri("ms-settings:display")) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
                && owner.Dispatches == 0, "ms-settings: is unavailable on macOS");
            await Task.Delay(1000);
            Check(!File.Exists(marker), "a refused URI started the application");

            // The dispatch behind an accepted URI, with the test scheme.
            var opened = await MacUriLauncher.Open(scheme + ":open/a%20b?c=d").WaitAsync(TimeSpan.FromSeconds(60));
            Console.WriteLine($"NSWorkspace openURL {scheme}: {opened}");
            Check(opened is PlatformResult<PlatformUnit>.Success, "NSWorkspace accepted the test URI");
            Check(await ApplicationsTests.StartsAsync(marker, 1), "the scheme's application started");

            // A scheme without an application is reported, not opened.
            var unregistered = await MacUriLauncher.Open("runic-unregistered-" + unique + ":x").WaitAsync(TimeSpan.FromSeconds(60));
            Console.WriteLine($"Unregistered scheme: {unregistered}");
            Check(unregistered is not PlatformResult<PlatformUnit>.Success, "a scheme without an application did not succeed");
            Console.WriteLine("PASS macOS URI launcher: refusals before dispatch, NSWorkspace through the URL handler.");
        }
        finally
        {
            if (bundle is not null) Console.WriteLine($"lsregister -u {bundle}: {ApplicationsTests.RunRegister("-u", bundle)}");
            if (Directory.Exists(installed)) Directory.Delete(installed, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Counts dispatches; a refused URI must not reach it.
    private sealed class CountingOwner : INativeWindowOwner
    {
        internal int Dispatches;
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => true;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
        { Dispatches++; return ValueTask.FromException(new OwnerClosedException()); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("URI launcher check failed: " + message);
    }
}
