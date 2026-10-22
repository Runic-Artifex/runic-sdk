using System.Collections.Immutable;
using Microsoft.Win32;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// Per-user registration of URL schemes and file types, in a scratch key under the current user that stands in for
// Software\Classes, Software and RegisteredApplications, and for the machine's classes: the user's real registrations
// are never read or written. The shell opening a registered scheme into the running instance is the Runtime suite's
// NativeOpen case (CI runners only).
internal sealed class OpenRegistrationTests
{
    [Test]
    public void ActivationItemsBecomeArguments()
    {
#pragma warning disable CA1416 // Merge is pure list handling.
        var protocol = WindowsActivation.Merge(["--flag"], (WindowsActivation.Protocol, ["org.example.notes:open?id=1"]));
        Check(protocol.Arguments.SequenceEqual(["--flag", "org.example.notes:open?id=1"]), string.Join(" | ", protocol.Arguments));
        var files = WindowsActivation.Merge([@"C:\a.notes"], (WindowsActivation.File, [@"c:\A.notes", @"C:\b.notes"]));
        Check(files.Arguments.SequenceEqual([@"C:\a.notes", @"C:\b.notes"]), "an item the arguments carry is not repeated: " + string.Join(" | ", files.Arguments));
        var launch = WindowsActivation.Merge(["--background"], (WindowsActivation.Launch, ImmutableArray<string>.Empty));
        Check(launch.Arguments.SequenceEqual(["--background"]), "a launch keeps its arguments");
        Check(WindowsActivation.Merge(["x"], null).Arguments.SequenceEqual(["x"]), "no activation");
#pragma warning restore CA1416
    }

    [Test, Explicit, Category("NativeLoginStartup")]
    public async Task RegistrationRoundTripAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Registration requires Windows.");
        string root = @"Software\Runic.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var registration = Create(root);
            var notes = new DesktopOpenRegistration("Runic Notes")
            {
                UrlSchemes = ["runic-notes"],
                FileTypes = [new(".runicnotes") { Description = "Runic notes" }],
                Executable = @"C:\Program Files\Runic Notes\Notes.exe",
            };
            Check(await registration.IsRegisteredAsync(notes) is PlatformResult<bool>.Success { Value: false });
            Check(await registration.RegisterAsync(notes) is PlatformResult<PlatformUnit>.Success);
            Check(await registration.IsRegisteredAsync(notes) is PlatformResult<bool>.Success { Value: true });
            using (var classes = Registry.CurrentUser.OpenSubKey(root + @"\Classes")!)
            {
                using var scheme = classes.OpenSubKey("runic-notes")!;
                Check(scheme.GetValue("") as string == "URL:Runic Notes" && scheme.GetValue("URL Protocol") as string == "");
                using (var command = scheme.OpenSubKey(@"shell\open\command")!)
                    Check(command.GetValue("") as string == "\"C:\\Program Files\\Runic Notes\\Notes.exe\" \"%1\"");
                using (var openWith = classes.OpenSubKey(@".runicnotes\OpenWithProgids")!)
                    Check(openWith.GetValueNames().SequenceEqual(["org.example.Notes.runicnotes"]));
                using (var extension = classes.OpenSubKey(".runicnotes")!)
                    Check(extension.GetValue("") is null, "the extension's default stays the user's choice");
                using var progId = classes.OpenSubKey("org.example.Notes.runicnotes")!;
                Check(progId.GetValue("") as string == "Runic notes");
            }
            using (var registered = Registry.CurrentUser.OpenSubKey(root + @"\RegisteredApplications")!)
                Check(registered.GetValue("org.example.Notes") as string == root + @"\Software\org.example.Notes\Capabilities");
            using (var capabilities = Registry.CurrentUser.OpenSubKey(root + @"\Software\org.example.Notes\Capabilities")!)
            using (var urls = capabilities.OpenSubKey("URLAssociations")!)
            using (var files = capabilities.OpenSubKey("FileAssociations")!)
                Check(urls.GetValue("runic-notes") as string == "runic-notes" && files.GetValue(".runicnotes") as string == "org.example.Notes.runicnotes");

            // Registering again, from a new location, updates the application's own keys.
            var moved = notes with { Executable = @"C:\Notes 2\Notes.exe" };
            Check(await registration.RegisterAsync(moved) is PlatformResult<PlatformUnit>.Success);
            Check(await registration.IsRegisteredAsync(moved) is PlatformResult<bool>.Success { Value: true });
            Check(await registration.IsRegisteredAsync(notes) is PlatformResult<bool>.Success { Value: false }, "the old command no longer points here");

            Check(await registration.UnregisterAsync(moved) is PlatformResult<bool>.Success { Value: true });
            Check(await registration.UnregisterAsync(moved) is PlatformResult<bool>.Success { Value: false }, "nothing left to remove");
            using (var classes = Registry.CurrentUser.OpenSubKey(root + @"\Classes")!)
                Check(classes.GetSubKeyNames().Length == 0, "every key is removed: " + string.Join(",", classes.GetSubKeyNames()));
            using (var registered = Registry.CurrentUser.OpenSubKey(root + @"\RegisteredApplications")!)
                Check(registered.GetValue("org.example.Notes") is null);
            Check(Registry.CurrentUser.OpenSubKey(root + @"\Software\org.example.Notes") is null);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
    }

    [Test, Explicit, Category("NativeLoginStartup")]
    public async Task ForeignEntriesAreNeverOverwrittenAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Registration requires Windows.");
        string root = @"Software\Runic.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var foreign = Registry.CurrentUser.CreateSubKey(root + @"\Classes\runic-foreign\shell\open\command"))
                foreign.SetValue("", "\"C:\\Other\\Other.exe\" \"%1\"");
            using (var machine = Registry.CurrentUser.CreateSubKey(root + @"\Machine\org.example.Notes.machine\shell\open\command"))
                machine.SetValue("", "\"C:\\Machine\\App.exe\" \"%1\"");
            var registration = Create(root);
            var notes = new DesktopOpenRegistration("Runic Notes") { UrlSchemes = ["runic-notes", "runic-foreign"], Executable = @"C:\Notes\Notes.exe" };
            var refused = await registration.RegisterAsync(notes);
            Check(refused is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Conflict, Diagnostic.Message: { } message }
                && message.Contains("runic-foreign", StringComparison.Ordinal), $"{refused}");
            using (var classes = Registry.CurrentUser.OpenSubKey(root + @"\Classes")!)
            {
                Check(classes.OpenSubKey("runic-notes") is null, "nothing is written when one item conflicts");
                using var command = classes.OpenSubKey(@"runic-foreign\shell\open\command")!;
                Check(command.GetValue("") as string == "\"C:\\Other\\Other.exe\" \"%1\"", "the foreign entry is unchanged");
            }
            Check(await registration.UnregisterAsync(notes) is PlatformResult<bool>.Success { Value: false }, "a foreign entry is not removed");
            using (var command = Registry.CurrentUser.OpenSubKey(root + @"\Classes\runic-foreign\shell\open\command")!)
                Check(command.GetValue("") is string, "the foreign entry survives unregistration");

            // A machine-wide ProgID of the same name is another application's too.
            var machineType = new DesktopOpenRegistration("Runic Notes") { FileTypes = [new(".machine")], Executable = @"C:\Notes\Notes.exe" };
            Check(await registration.RegisterAsync(machineType) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Conflict });
            // The same command, registered by hand before, is this application's.
            using (var same = Registry.CurrentUser.CreateSubKey(root + @"\Classes\runic-same\shell\open\command"))
                same.SetValue("", "\"C:\\Notes\\Notes.exe\" \"%1\"");
            Check(await registration.RegisterAsync(notes with { UrlSchemes = ["runic-same"] }) is PlatformResult<PlatformUnit>.Success);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
    }

    [Test, Explicit, Category("NativeLoginStartup")]
    public async Task PackagedRegistrationIsUnavailableAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Registration requires Windows.");
        var packaged = WindowsPlatformProvider.CreateOpenRegistration("org.example.Notes", "Example.Notes_1a2b3c4d5e6f7!App");
        Check(await packaged.RegisterAsync(new DesktopOpenRegistration("Notes") { UrlSchemes = ["runic-notes"] })
            is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable });
        Check(WindowsPlatformProvider.CreateOpenRegistration("org.example.Notes", null) is WindowsOpenRegistration);
        Check(WindowsPlatformProvider.GetLaunch(["a", "b"]).Arguments.SequenceEqual(["a", "b"]), "an unpackaged launch is its arguments");
        foreach (string invalid in new[] { "", "a\\b", "a\"b", new string('a', 129) })
        {
            try { WindowsPlatformProvider.CreateOpenRegistration(invalid); throw new InvalidOperationException($"'{invalid}' was accepted."); }
            catch (ArgumentException) { }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static WindowsOpenRegistration Create(string root) => new("org.example.Notes", Registry.CurrentUser, root + @"\Classes", root + @"\Software",
        root + @"\RegisteredApplications", () => Registry.CurrentUser.OpenSubKey(root + @"\Machine"));

    private static void Check(bool condition, string message = "check failed") { if (!condition) throw new InvalidOperationException(message); }
}
