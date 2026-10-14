using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TUnit.Core;
using static Runic.Platform.MacOS.Tests.PrivilegedModelTests;

namespace Runic.Platform.MacOS.Tests;

/// <summary>
/// The daemon as root under launchd, reached through the system domain's Mach service like a registered daemon, with
/// real Authorization Services rights. Needs passwordless sudo (the macOS CI runner has it): the case installs this
/// executable as a launch daemon with <c>launchctl bootstrap system</c> (SMAppService registration needs a signed
/// bundle and the user's approval, which CI cannot give), creates a temporary administrator whose password it passes
/// in the authorization environment instead of the dialog, and removes both afterwards. Select with
/// <c>--treenode-filter "/*/*/*/*[Category=NativePrivilegedRoot]"</c>.
/// </summary>
internal sealed class PrivilegedRootTests
{
    private const string Label = "eu.runic.tests.privileged-root";
    private const string User = "runicauthtest";
    private const string Log = "/tmp/eu.runic.tests.privileged-root.log";
    private static readonly PrivilegedActionDeclaration RootId = new("eu.runic.tests.root-id", "/usr/bin/id") { Verb = "-u" };
    private static readonly PrivilegedActionDeclaration RootEnv = new("eu.runic.tests.root-env", "/usr/bin/env");
    private static readonly PrivilegedActionDeclaration RootShell = new("eu.runic.tests.root-shell", "/bin/sh") { Verb = "-c", MaximumArguments = 2 };
    private static readonly PrivilegedActionDeclaration[] Actions = [RootId, RootEnv, RootShell];

    // The application is this executable and the daemon the same executable inside an application bundle whose
    // identifier is this executable's signing identifier, so each requires the other's identifier. (The bundle's
    // ad hoc signature has another code directory hash, so the designated requirement would not do.)
    private static MacPrivilegedDaemonDefinition Definition()
    {
        var self = $"identifier \"{MacSecurity.SelfIdentifier() ?? throw new InvalidOperationException("The test executable has no signing identifier.")}\"";
        return new(Label, "ABCDE12345", "eu.runic.tests", Label, Actions.Select(a => new MacPrivilegedRight(a.Id, $"Runic tests want to run {a.Id} as root.")))
        {
            ApplicationRequirement = self,
            DaemonRequirement = self,
        };
    }

    /// <summary>The daemon's process, started by launchd as root.</summary>
    internal static int RunDaemon()
    {
        var exitCode = MacPrivilegedDaemon.Run(Actions, Definition());
        Console.Error.WriteLine($"MacPrivilegedDaemon.Run returned {exitCode}.");
        return exitCode;
    }

    [Test, Explicit, Category("NativePrivilegedRoot"), DisplayName("root: a launchd daemon runs actions as root after Authorization Services checks")]
    public async Task RootDaemonAsync()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Check(Sudo("true").ExitCode == 0, "passwordless sudo is required");
        Check(!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported, "the daemon runs only as NativeAOT: run the published executable");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var testRights = new List<string>();
        var definition = Definition();
        try
        {
            CreateAdministrator(password);
            // A right someone defined first, with keys the daemon's rule does not have.
            _ = Sudo("security", "authorizationdb", "remove", RootId.Id);
            var weak = Path.GetTempFileName();
            File.WriteAllText(weak, """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0"><dict>
                  <key>class</key><string>user</string><key>group</key><string>admin</string>
                  <key>shared</key><true/><key>timeout</key><integer>300</integer><key>tries</key><integer>1</integer>
                  <key>extract-password</key><true/>
                </dict></plist>
                """);
            var (written, writeOutput) = Sudo("sh", "-c", $"security authorizationdb write {RootId.Id} < '{weak}'");
            File.Delete(weak);
            Check(written == 0, "pre-define a weaker right: " + writeOutput);
            var (created, creator) = Origin(RootId.Id);
            Install();
            var host = new CredentialHost(definition, User, password);
            var provider = new MacPrivilegedProvider(Actions, definition, host);

            var lines = new List<string>();
            var result = await provider.RunAsync(new(RootId, []) { Output = l => lines.Add(l.Text) }).AsTask().WaitAsync(TimeSpan.FromMinutes(2));
            Check(result is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 0 } && lines.SequenceEqual(["0"]), $"root: {Describe(result)} {string.Join(",", lines)}");
            Console.WriteLine("PASS id -u as root through the system Mach service, with the right obtained by credentials and checked again by the daemon");

            lines.Clear();
            Environment.SetEnvironmentVariable("RUNIC_TEST_SECRET", "secret");
            result = await provider.RunAsync(new(RootEnv, []) { Output = l => lines.Add(l.Text) }).AsTask().WaitAsync(TimeSpan.FromMinutes(2));
            string[] environment = ["PATH=/usr/bin:/bin:/usr/sbin:/sbin", "HOME=/var/root", "USER=root", "LOGNAME=root", $"RUNIC_CALLER_UID={MacProcess.GetEffectiveUserId()}"];
            Check(result is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 0 } && lines.Order().SequenceEqual(environment.Order()), "environment: " + string.Join(",", lines));

            lines.Clear();
            result = await provider.RunAsync(new(RootShell, ["umask; pwd; id -u; exit 4"]) { Output = l => lines.Add(l.Text) }).AsTask().WaitAsync(TimeSpan.FromMinutes(2));
            Check(result is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 4 } && lines.SequenceEqual(["0022", "/", "0"]), $"process state: {Describe(result)} {string.Join(",", lines)}");
            Console.WriteLine("PASS environment, umask 022, working directory / and exit code as root");

            // The daemon defined every right as the rule in MacAuthorizationRule, with the action's message.
            foreach (var right in definition.Rights)
            {
                var stored = MacSecurity.CopyRight(right.ActionId);
                Check(stored != 0, $"{right.ActionId} is defined");
                try { Check(MacSystemAuthority.Matches(stored, right), $"{right.ActionId} is the daemon's rule"); }
                finally { MacSecurity.Release(stored); }
            }
            var replaced = MacSecurity.CopyRight(RootId.Id);
            try
            {
                var keys = MacSecurity.Keys(replaced);
                Console.WriteLine("stored keys of the replaced right: " + string.Join(", ", keys));
                Check(!keys.Contains("extract-password") && MacSecurity.ToNumber(MacSecurity.Get(replaced, "tries")) == 10000
                    && MacSecurity.ToBoolean(MacSecurity.Get(replaced, "shared")) == false, "the pre-defined right's extra keys and values are gone");
            }
            finally { MacSecurity.Release(replaced); }
            // In place: authd keeps a rule's creation time and creator on an update; a removal and a new rule would not.
            var (replacedCreated, replacedCreator) = Origin(RootId.Id);
            Console.WriteLine($"created {created} by {creator}; after the daemon: created {replacedCreated} by {replacedCreator}");
            Check(replacedCreated == created && replacedCreator == creator, "the pre-defined right was updated in place, never removed");
            Console.WriteLine("PASS rights defined by the daemon: class user, group admin, shared false, timeout 30, the action's prompt; a pre-defined weaker right was replaced in place");

            // The daemon runs from an application bundle with localizations; its stored rights must match its
            // definition exactly, so further runs leave them alone.
            var modified = Modified(RootId.Id);
            for (var run = 0; run < 3; run++)
            {
                result = await provider.RunAsync(new(RootId, [])).AsTask().WaitAsync(TimeSpan.FromMinutes(2));
                Check(result is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 0 }, $"run {run}: {Describe(result)}");
            }
            Check(Modified(RootId.Id) == modified && modified is not null, $"the right is not defined again on every run: {modified} then {Modified(RootId.Id)}");
            Console.WriteLine("PASS the daemon in a bundle with en.lproj leaves a matching right unchanged across runs");

            host.Mode = CredentialMode.WrongPassword;
            result = await provider.RunAsync(new(RootId, [])).AsTask().WaitAsync(TimeSpan.FromMinutes(2));
            Console.WriteLine($"wrong password: {Describe(result)} {(result as PlatformResult<PrivilegedActionCompletion>.Failed)?.Diagnostic}");
            Check(result is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.PermissionDenied, Diagnostic.Code: -60005 }, "a wrong password is denied (-60005)");

            host.Mode = CredentialMode.NoCredential;
            result = await provider.RunAsync(new(RootId, [])).AsTask().WaitAsync(TimeSpan.FromMinutes(2));
            Console.WriteLine($"no credential: {Describe(result)} {(result as PlatformResult<PrivilegedActionCompletion>.Failed)?.Diagnostic}");
            Check(result is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.PermissionDenied, Diagnostic.Message: not null } failed
                && failed.Diagnostic!.Message!.Contains("daemon", StringComparison.Ordinal), "the daemon refuses an authorization without the right");

            host.Mode = CredentialMode.Stale;
            result = await provider.RunAsync(new(RootId, [])).AsTask().WaitAsync(TimeSpan.FromMinutes(2));
            Console.WriteLine($"stale external form: {Describe(result)} {(result as PlatformResult<PrivilegedActionCompletion>.Failed)?.Diagnostic}");
            Check(result is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.PermissionDenied, Diagnostic.Message: not null } stale
                && stale.Diagnostic!.Message!.Contains("daemon", StringComparison.Ordinal), "the daemon refuses the form of a finished run");
            Console.WriteLine("PASS a wrong password, an authorization without the right and a finished run's form are denied");

            // The evidence for the rule's timeout: authd compares a credential's age with the timeout when it checks a
            // right again without interaction, so with 0 the daemon's check fails and with 30 it succeeds.
            foreach (var timeout in new[] { 0L, 30L })
            {
                var name = $"eu.runic.tests.timeout-{timeout}-{Guid.NewGuid():N}"[..48];
                testRights.Add(name);
                Check(DefineRight(name, timeout) == 0, $"anyone may add the right {name}");
                using var grant = CredentialHost.AuthorizeWithCredentials(name, User, password);
                Check(grant.Status == 0, $"obtained {name} with credentials: {grant.Status}");
                await Task.Delay(2000);
                var check = MacSystemAuthority.Instance.Check(name, grant.ExternalForm!);
                Console.WriteLine($"timeout {timeout}: the check without interaction 2 s later returned {check}");
                Check(timeout == 0 ? check != 0 : check == 0, $"timeout {timeout}: {check}");
            }
            Console.WriteLine("PASS timeout 0 fails the daemon's check of a right obtained 2 s earlier; timeout 30 passes");
        }
        catch
        {
            Console.WriteLine("Daemon log:\n" + Sudo("cat", Log).Output);
            throw;
        }
        finally
        {
            _ = Sudo("launchctl", "bootout", "system/" + Label);
            _ = Sudo("rm", "-f", $"/Library/LaunchDaemons/{Label}.plist", Log);
            if (Directory.Exists(Path.Combine("/private/tmp", Label))) Directory.Delete(Path.Combine("/private/tmp", Label), recursive: true);
            _ = Sudo("dscl", ".", "-delete", "/Users/" + User);
            foreach (var right in definition.Rights.Select(r => r.ActionId).Concat(testRights))
                _ = Sudo("security", "authorizationdb", "remove", right);
        }
    }

    private static void CreateAdministrator(string password)
    {
        _ = Sudo("dscl", ".", "-delete", "/Users/" + User);
        foreach (var command in new[]
        {
            new[] { "dscl", ".", "-create", "/Users/" + User },
            ["dscl", ".", "-create", "/Users/" + User, "UserShell", "/usr/bin/false"],
            ["dscl", ".", "-create", "/Users/" + User, "RealName", "Runic authorization test"],
            ["dscl", ".", "-create", "/Users/" + User, "UniqueID", "4987"],
            ["dscl", ".", "-create", "/Users/" + User, "PrimaryGroupID", "20"],
            ["dscl", ".", "-create", "/Users/" + User, "NFSHomeDirectory", "/var/empty"],
            ["dscl", ".", "-passwd", "/Users/" + User, password],
            ["dseditgroup", "-o", "edit", "-a", User, "-t", "user", "admin"],
        })
        {
            var (exit, output) = Sudo(command);
            Check(exit == 0, $"{string.Join(' ', command.Take(4))}: {output}");
        }
    }

    private static (double? Created, string? Creator) Origin(string name)
    {
        var right = MacSecurity.CopyRight(name);
        Check(right != 0, $"{name} exists");
        try { return (MacSecurity.ToDouble(MacSecurity.Get(right, "created")), MacSecurity.ToText(MacSecurity.Get(right, "identifier"))); }
        finally { MacSecurity.Release(right); }
    }

    private static double? Modified(string name)
    {
        var right = MacSecurity.CopyRight(name);
        try { return right == 0 ? null : MacSecurity.ToDouble(MacSecurity.Get(right, "modified")); }
        finally { if (right != 0) MacSecurity.Release(right); }
    }

    /// <summary>
    /// A real application bundle around a copy of this executable, with an <c>en.lproj</c> and this executable's signing
    /// identifier as its bundle identifier, signed ad hoc with the hardened runtime.
    /// </summary>
    private static string BuildBundle()
    {
        var identifier = MacSecurity.SelfIdentifier()!;
        var name = Path.GetFileName(Environment.ProcessPath!);
        var bundle = Path.Combine("/private/tmp", Label, "RunicPrivilegedTests.app");
        if (Directory.Exists(Path.GetDirectoryName(bundle))) Directory.Delete(Path.GetDirectoryName(bundle)!, recursive: true);
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources", "en.lproj"));
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources", "de.lproj"));
        File.Copy(Environment.ProcessPath!, Path.Combine(bundle, "Contents", "MacOS", name));
        File.WriteAllText(Path.Combine(bundle, "Contents", "Resources", "en.lproj", "Localizable.strings"), "\"greeting\" = \"Hello\";\n");
        File.WriteAllText(Path.Combine(bundle, "Contents", "Resources", "de.lproj", "Localizable.strings"), "\"greeting\" = \"Hallo\";\n");
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>CFBundleIdentifier</key><string>{identifier}</string>
              <key>CFBundleExecutable</key><string>{name}</string>
              <key>CFBundleName</key><string>RunicPrivilegedTests</string>
              <key>CFBundlePackageType</key><string>APPL</string>
              <key>CFBundleDevelopmentRegion</key><string>en</string>
              <key>CFBundleLocalizations</key><array><string>en</string><string>de</string></array>
            </dict></plist>
            """);
        var start = new ProcessStartInfo("/usr/bin/codesign") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "--force", "--sign", "-", "--options=runtime", bundle }) start.ArgumentList.Add(argument);
        using var codesign = Process.Start(start)!;
        var error = codesign.StandardError.ReadToEnd();
        codesign.WaitForExit();
        Check(codesign.ExitCode == 0, "codesign: " + error);
        return Path.Combine(bundle, "Contents", "MacOS", name);
    }

    private static void Install()
    {
        // launchd starts the daemon from the bundle, so its main bundle is the application's, with localizations.
        List<string> program = [BuildBundle(), "--privileged-daemon"];
        var plist = Path.GetTempFileName();
        File.WriteAllText(plist, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{Label}</string>
              <key>ProgramArguments</key><array>{string.Concat(program.Select(p => $"<string>{System.Security.SecurityElement.Escape(p)}</string>"))}</array>
              <key>MachServices</key><dict><key>{Label}</key><true/></dict>
              <key>StandardOutPath</key><string>{Log}</string>
              <key>StandardErrorPath</key><string>{Log}</string>
            </dict>
            </plist>
            """);
        var installed = $"/Library/LaunchDaemons/{Label}.plist";
        _ = Sudo("launchctl", "bootout", "system/" + Label);
        var (exit, output) = Sudo("install", "-m", "644", "-o", "root", "-g", "wheel", plist, installed);
        Check(exit == 0, "install: " + output);
        (exit, output) = Sudo("launchctl", "bootstrap", "system", installed);
        Check(exit == 0, "launchctl bootstrap: " + output);
        File.Delete(plist);
    }

    private static unsafe int DefineRight(string name, long timeout)
    {
        var status = MacSecurity.AuthorizationCreate(null, null, 0, out var authorization);
        if (status != 0) return status;
        var definition = MacSystemAuthority.CreateDefinition(new MacPrivilegedRight(name, "Runic tests check the credential timeout."));
        MacSecurity.Set(definition, "timeout", MacSecurity.CreateNumber(timeout));
        try { return MacSecurity.AuthorizationRightSet(authorization, name, definition, 0, 0, 0); }
        finally
        {
            MacSecurity.Release(definition);
            _ = MacSecurity.AuthorizationFree(authorization, 0);
        }
    }

    private static (int ExitCode, string Output) Sudo(params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/sudo") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-n");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Result + error);
    }

    private enum CredentialMode { Credentials, WrongPassword, NoCredential, Stale }

    /// <summary>The production XPC connection, with the right obtained from credentials in the environment instead of the dialog.</summary>
    private sealed class CredentialHost(MacPrivilegedDaemonDefinition definition, string user, string password) : MacPrivilegedHost
    {
        private byte[]? _lastForm;
        public CredentialMode Mode { get; set; }

        internal override PlatformUnavailableReason? CheckAvailable(MacPrivilegedDaemonDefinition daemon) => null;

        internal override IMacPrivilegedChannel Connect(MacPrivilegedDaemonDefinition daemon) =>
            MacXpcChannel.ConnectService(definition.Label, definition.DaemonRequirement);

        internal override IReadOnlyDictionary<string, string> PassedEnvironment() => new Dictionary<string, string>();

        internal override MacAuthorizationGrant Authorize(string right)
        {
            switch (Mode)
            {
                case CredentialMode.WrongPassword:
                    return AuthorizeWithCredentials(right, user, password + "x");
                case CredentialMode.NoCredential:
                    return WithoutRight();
                case CredentialMode.Stale:
                    return new MacAuthorizationGrant(0, _lastForm ?? new byte[32], null);
                default:
                    var grant = AuthorizeWithCredentials(right, user, password);
                    _lastForm = grant.ExternalForm;
                    return grant;
            }
        }

        // An authorization that never asked for the right, as a client that skips the dialog would send.
        private static unsafe MacAuthorizationGrant WithoutRight()
        {
            var status = MacSecurity.AuthorizationCreate(null, null, 0, out var authorization);
            if (status != 0) return new MacAuthorizationGrant(status, null, null);
            var form = new byte[MacSecurity.ExternalFormLength];
            fixed (byte* bytes = form) status = MacSecurity.AuthorizationMakeExternalForm(authorization, bytes);
            return new MacAuthorizationGrant(status, form, () => { _ = MacSecurity.AuthorizationFree(authorization, MacSecurity.FlagDestroyRights); });
        }

        internal static unsafe MacAuthorizationGrant AuthorizeWithCredentials(string right, string user, string password)
        {
            var status = MacSecurity.AuthorizationCreate(null, null, 0, out var authorization);
            if (status != 0) return new MacAuthorizationGrant(status, null, null);
            var grant = () => { _ = MacSecurity.AuthorizationFree(authorization, MacSecurity.FlagDestroyRights); };
            var strings = new[] { right, "username", user, "password", password }.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            try
            {
                var item = new MacSecurity.AuthorizationItem { Name = strings[0] };
                var rights = new MacSecurity.AuthorizationItemSet { Count = 1, Items = &item };
                var environment = stackalloc MacSecurity.AuthorizationItem[2];
                environment[0] = new() { Name = strings[1], Value = strings[2], ValueLength = (nuint)System.Text.Encoding.UTF8.GetByteCount(user) };
                environment[1] = new() { Name = strings[3], Value = strings[4], ValueLength = (nuint)System.Text.Encoding.UTF8.GetByteCount(password) };
                var set = new MacSecurity.AuthorizationItemSet { Count = 2, Items = environment };
                status = MacSecurity.AuthorizationCopyRights(authorization, &rights, &set, MacSecurity.FlagExtendRights, 0);
            }
            finally
            {
                foreach (var pointer in strings) Marshal.FreeCoTaskMem(pointer);
            }
            if (status != 0) return new MacAuthorizationGrant(status, null, grant);
            var form = new byte[MacSecurity.ExternalFormLength];
            fixed (byte* bytes = form) status = MacSecurity.AuthorizationMakeExternalForm(authorization, bytes);
            return new MacAuthorizationGrant(status, status == 0 ? form : null, grant);
        }
    }
}
