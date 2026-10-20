using System.Collections.Immutable;
using System.Text;
using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

// The portable parts of privileged actions on macOS: definitions, outcomes, the authorization rule, the program
// checks, the daemon's request checks and the provider's flow against a fake daemon. PrivilegedNativeTests runs the
// XPC, Authorization Services and posix_spawn paths on macOS.
internal sealed class PrivilegedModelTests
{
    internal static readonly PrivilegedActionDeclaration Shell = new("eu.runic.tests.shell", "/bin/sh") { Verb = "-c", MaximumArguments = 3 };
    internal static readonly PrivilegedActionDeclaration Id = new("eu.runic.tests.id", "/usr/bin/id") { Verb = "-u" };

    internal static MacPrivilegedDaemonDefinition Definition(params PrivilegedActionDeclaration[] actions) =>
        new("eu.runic.tests.privileged", "ABCDE12345", "eu.runic.tests", "eu.runic.tests.privileged",
            actions.Select(a => new MacPrivilegedRight(a.Id, $"Runic tests want to run {a.Id}.")));

    [Test, DisplayName("definition: requirements name the Team ID and identifiers")]
    public void DefinitionRequirements()
    {
        var definition = Definition(Shell);
        Check(definition.PlistName == "eu.runic.tests.privileged.plist", definition.PlistName);
        // Apple's Developer ID form: the Developer ID intermediate and a Developer ID Application leaf.
        const string DeveloperId = " and certificate 1[field.1.2.840.113635.100.6.2.6] exists and certificate leaf[field.1.2.840.113635.100.6.1.13] exists";
        const string Team = " and certificate leaf[subject.OU] = \"ABCDE12345\"";
        Check(definition.ApplicationRequirement == "anchor apple generic and identifier \"eu.runic.tests\"" + DeveloperId + Team, definition.ApplicationRequirement);
        Check(definition.DaemonRequirement == "anchor apple generic and identifier \"eu.runic.tests.privileged\"" + DeveloperId + Team, definition.DaemonRequirement);
        Check(definition.ProgramRequirement == "anchor apple generic" + DeveloperId + Team, definition.ProgramRequirement);
        Check(!definition.ApplicationRequirement.Contains("6.1.9", StringComparison.Ordinal), "no Mac App Store alternative");
        Check(!definition.DevelopmentSigning, "distribution signing by default");
        var development = new MacPrivilegedDaemonDefinition("eu.runic.tests.privileged", "ABCDE12345", "eu.runic.tests", "eu.runic.tests.privileged", []) { DevelopmentSigning = true };
        Check(development.ApplicationRequirement == "anchor apple generic and identifier \"eu.runic.tests\" and certificate leaf[subject.OU] = \"ABCDE12345\"", "development signing drops the marker");
        var withIdentifier = new MacPrivilegedRight(Shell.Id, "m") { ProgramIdentifier = "eu.runic.tests.helper" };
        Check(definition.RequirementFor(withIdentifier) == $"({definition.ProgramRequirement}) and identifier \"eu.runic.tests.helper\"", "a program's requirement names its identifier");
        Check(definition.RequirementFor(definition.Rights[0]) is null, "no identifier, no requirement");
        Check(ReferenceEquals(definition.FindRight(Shell.Id), definition.Rights[0]) && definition.FindRight("eu.runic.tests.none") is null, "FindRight");
    }

    [Test, DisplayName("definition: invalid names, duplicate rights and reserved right names are rejected")]
    public void DefinitionValidation()
    {
        MacPrivilegedRight[] rights = [new("eu.runic.tests.a", "m")];
        Throws<ArgumentException>(() => _ = new MacPrivilegedDaemonDefinition("label", "ABCDE12345", "eu.app", "eu.daemon", rights), "one-segment label");
        Throws<ArgumentException>(() => _ = new MacPrivilegedDaemonDefinition("eu.label", "abcde12345", "eu.app", "eu.daemon", rights), "lowercase Team ID");
        Throws<ArgumentException>(() => _ = new MacPrivilegedDaemonDefinition("eu.label", "ABCDE1234", "eu.app", "eu.daemon", rights), "short Team ID");
        Throws<ArgumentException>(() => _ = new MacPrivilegedDaemonDefinition("eu.label", "ABCDE12345", "eu.\"app", "eu.daemon", rights), "quote in identifier");
        Throws<ArgumentException>(() => _ = new MacPrivilegedDaemonDefinition("eu.label", "ABCDE12345", "eu.app", "eu.daemon", [rights[0], rights[0]]), "duplicate right");
        foreach (var reserved in new[] { "system.privilege.admin", "config.add.example", "com.apple.example", "authenticate-admin.x", "admin.example", "allow.example", "default.example" })
            Throws<ArgumentException>(() => _ = new MacPrivilegedRight(reserved, "m"), reserved);
        Throws<ArgumentException>(() => _ = new MacPrivilegedRight("eu.runic.tests.a", ""), "empty message");
        foreach (var program in new[] { "", "Contents/../helper", "Contents//helper", "/usr/./bin/x", "/" })
            Throws<ArgumentException>(() => _ = new MacPrivilegedRight("eu.runic.tests.a", "m") { Program = program }, $"program '{program}'");
        Check(new MacPrivilegedRight("eu.runic.tests.a", "m") { Program = "Contents/MacOS/helper" }.Program == "Contents/MacOS/helper", "bundle program");
        Check(new MacPrivilegedRight("eu.runic.tests.a", "m") { Program = "/usr/local/libexec/helper" }.Program == "/usr/local/libexec/helper", "absolute program");
        Throws<ArgumentException>(() => _ = new MacPrivilegedRight("eu.runic.tests.a", "m") { ProgramIdentifier = "helper\"" }, "program identifier");
        Throws<ArgumentException>(() => _ = new MacPrivilegedDaemonDefinition("eu.label", "ABCDE12345", "eu.app", "eu.daemon", rights) { ApplicationRequirement = "" }, "empty requirement");
    }

    [Test, DisplayName("generated: MacOSDaemon carries the items' label, identifiers, messages and programs")]
    public void GeneratedDefinition()
    {
        var daemon = PrivilegedActions.MacOSDaemon;
        Check(daemon.Label == "eu.runic.tests.privileged" && daemon.TeamIdentifier == "ABCDE12345" && daemon.ApplicationIdentifier == "eu.runic.tests"
            && daemon.DaemonIdentifier == "eu.runic.tests.privileged", "names");
        Check(daemon.Rights.Select(r => r.ActionId).SequenceEqual(["eu.runic.tests.activate", "eu.runic.tests.bundled", "eu.runic.tests.absolute"]), "rights in order");
        Check(daemon.Rights[0].Message == "Runic tests want to activate <the \"system\"> & restart." && daemon.Rights[0].Program is null, "escaped message");
        Check(daemon.Rights[1].Program == "Contents/MacOS/helper" && daemon.Rights[2].Program == "/Library/Application Support/Runic Tests/helper", "MacProgram");
        Check(daemon.Rights[1].ProgramIdentifier == "eu.runic.tests.helper" && daemon.Rights[2].ProgramIdentifier is null && !daemon.DevelopmentSigning, "MacProgramIdentifier");
        Check(PrivilegedActions.All.Select(a => a.Id).SequenceEqual(daemon.Rights.Select(r => r.ActionId)), "the shared declarations and the macOS rights come from the same items");
    }

    [Test, DisplayName("outcomes: Authorization Services results")]
    [Arguments(-60006, "Failed:UserDismissed")]
    [Arguments(-60005, "Failed:PermissionDenied")]
    [Arguments(-60007, "Unavailable:BackendUnavailable")]
    [Arguments(-60008, "Failed:Other")]
    [Arguments(-60002, "Failed:Other")]
    public void AuthorizationOutcomes(int status, string expected)
    {
        var result = MacPrivilegedOutcome.FromAuthorization(status);
        Check(Describe(result!) == expected, $"{status}: {Describe(result!)}");
        if (result is PlatformResult<PrivilegedActionCompletion>.Failed failed)
            Check(failed.Diagnostic is { Domain: MacPrivilegedOutcome.AuthorizationDomain } diagnostic && diagnostic.Code == status, "the OSStatus is kept");
        Check(MacPrivilegedOutcome.FromAuthorization(0) is null, "success continues");
    }

    [Test, DisplayName("outcomes: the daemon's replies")]
    public void ReplyOutcomes()
    {
        Check(MacPrivilegedOutcome.FromReply(new(MacReplyStatus.Ran, 3)) is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 3 }, "ran");
        Check(Describe(MacPrivilegedOutcome.FromReply(new(MacReplyStatus.Denied, -60005))) == "Failed:PermissionDenied", "denied by the daemon's check");
        Check(MacPrivilegedOutcome.FromReply(new(MacReplyStatus.Untrusted, 1)) is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.InvalidData, Diagnostic.Code: 1 }, "missing");
        Check(MacPrivilegedOutcome.FromReply(new(MacReplyStatus.Untrusted, 3)) is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.InvalidData, Diagnostic.Code: 3 }, "untrusted");
        Check(Describe(MacPrivilegedOutcome.FromReply(new(MacReplyStatus.Rejected))) == "Failed:InvalidData", "rejected");
        Check(Describe(MacPrivilegedOutcome.FromReply(new(MacReplyStatus.UntrustedDaemon))) == "Failed:InvalidData", "untrusted daemon");
        Check(Describe(MacPrivilegedOutcome.FromReply(new(MacReplyStatus.Unreachable))) == "Unavailable:BackendUnavailable", "no daemon");
        Check(Describe(MacPrivilegedOutcome.FromReply(new(MacReplyStatus.Interrupted))) == "Failed:IOError", "broken connection");
        Check(Describe(MacPrivilegedOutcome.FromReply(new(MacReplyStatus.Failed, 2))) == "Failed:Other", "spawn failure");
        Check(Describe(MacPrivilegedOutcome.FromReply(new(MacReplyStatus.Invalid))) == "Failed:Other", "unknown reply");
        Check(Describe(MacPrivilegedOutcome.FromReply(new(MacReplyStatus.Busy))) == "Failed:ResourceBusy", "too many runs");
        foreach (var code in new[] { 8L, 9L, 10L })
            Check(MacPrivilegedOutcome.FromReply(new(MacReplyStatus.Untrusted, code)) is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.InvalidData } failed && failed.Diagnostic!.Code == code, $"program code {code}");
        Throws<ArgumentException>(() => MacPrivilegedOutcome.FromReply(new(MacReplyStatus.Cancelled)), "cancelled has no result");
        foreach (var status in new[] { MacReplyStatus.Ready, MacReplyStatus.Ran, MacReplyStatus.Denied, MacReplyStatus.Untrusted, MacReplyStatus.Rejected, MacReplyStatus.Cancelled, MacReplyStatus.Failed, MacReplyStatus.Busy })
            Check(MacReply.Parse(MacReply.Name(status)) == status, $"wire name of {status}");
        Check(MacReply.Parse("other") is null && MacReply.Parse(null) is null, "unknown wire names");
    }

    [Test, DisplayName("outcomes: SMAppService statuses and wait statuses")]
    public void ServiceAndExitStatus()
    {
        Check(MacPrivilegedOutcome.FromServiceStatus(0) == MacPrivilegedDaemonStatus.NotRegistered, "0");
        Check(MacPrivilegedOutcome.FromServiceStatus(1) == MacPrivilegedDaemonStatus.Enabled, "1");
        Check(MacPrivilegedOutcome.FromServiceStatus(2) == MacPrivilegedDaemonStatus.RequiresApproval, "2");
        Check(MacPrivilegedOutcome.FromServiceStatus(3) == MacPrivilegedDaemonStatus.NotFound, "3");
        Check(MacPrivilegedOutcome.FromServiceStatus(9) == MacPrivilegedDaemonStatus.Unavailable, "unknown");
        Check(MacPrivilegedOutcome.ExitCode(3 << 8) == 3 && MacPrivilegedOutcome.ExitCode(0) == 0, "exit codes");
        Check(MacPrivilegedOutcome.ExitCode(9) == 137 && MacPrivilegedOutcome.ExitCode(15 | 0x80) == 143, "signals, with and without a core dump");
    }

    [Test, DisplayName("rule: an administrator, not shared, a 30-second credential, never root without authentication")]
    public void AuthorizationRule()
    {
        var rule = MacAuthorizationRule.Definition("eu.runic.tests.shell").ToDictionary(p => p.Key, p => p.Value);
        Check((string)rule["class"] == "user" && (string)rule["group"] == "admin", "an administrator");
        Check(!(bool)rule["shared"], "not shared: the credential stays in this authorization");
        Check((long)rule["timeout"] == 30, "30 seconds: authd rejects any credential older than the timeout when the daemon checks again");
        Check((bool)rule["authenticate-user"] && !(bool)rule["allow-root"] && !(bool)rule["session-owner"], "always authenticate");
        Check((long)rule["tries"] == 10000, "authd's default number of tries, which it reports on every rule");
        Check(MacAuthorizationRule.MetadataKeys.SequenceEqual(["version", "created", "modified", "identifier", "requirement"]), "only authd's own metadata is tolerated");
        Check(((string)rule["comment"]).Contains("eu.runic.tests.shell", StringComparison.Ordinal), "comment names the action");
    }

    [Test, DisplayName("program: only a path that root alone can change runs without a signature")]
    public void ProgramTrust()
    {
        const ushort Dir = 0x4000, File = 0x8000, Link = 0xA000;
        var tree = new Dictionary<string, MacProcess.FileStatus>
        {
            ["/"] = new(Dir | 0x1ED, 0, 0),
            ["/usr"] = new(Dir | 0x1ED, 0, 0),
            ["/usr/bin"] = new(Dir | 0x1ED, 0, 0),
            ["/usr/bin/tool"] = new(File | 0x1ED, 0, 0),
            ["/usr/bin/data"] = new(File | 0x1A4, 0, 0),
            ["/usr/bin/writable"] = new(File | 0x1FF, 0, 0),
            ["/usr/bin/user"] = new(File | 0x1ED, 501, 20),
            ["/usr/bin/link"] = new(Link | 0x1ED, 0, 0),
            ["/Applications"] = new(Dir | 0x1FD, 0, 80), // root:admin 775
            ["/Applications/Example.app"] = new(Dir | 0x1ED, 501, 80),
            ["/Applications/Example.app/helper"] = new(File | 0x1ED, 501, 80),
            ["/var"] = new(Link | 0x1ED, 0, 0),
            ["/sticky"] = new(Dir | 0x3FD, 0, 0), // 1775, as /nix/store
            ["/sticky/tool"] = new(File | 0x1ED, 0, 0),
        };
        MacProcess.FileStatus? Lstat(string path) => tree.TryGetValue(path, out var status) ? status : null;
        var acls = new HashSet<string>();
        MacProgramCheck Trust(string path) => MacProgramTrust.Check(path, Lstat, acls.Contains);
        Check(Trust("/usr/bin/tool") == MacProgramCheck.RootOwned, "root-owned");
        Check(Trust("/sticky/tool") == MacProgramCheck.RootOwned, "a sticky group-writable directory");
        Check(Trust("/usr/bin/writable") == MacProgramCheck.NeedsSignature, "a world-writable program");
        Check(Trust("/usr/bin/user") == MacProgramCheck.NeedsSignature, "owned by a user");
        Check(Trust("/usr/bin/link") == MacProgramCheck.NeedsSignature, "a symbolic link");
        Check(Trust("/Applications/Example.app/helper") == MacProgramCheck.NeedsSignature, "admin-writable /Applications");
        Check(Trust("/var/tool") == MacProgramCheck.Missing, "nothing behind the link");
        Check(Trust("/usr/bin/missing") == MacProgramCheck.Missing, "missing");
        Check(Trust("/usr/bin/data") == MacProgramCheck.Missing, "not executable");
        Check(Trust("/usr/bin/tool/x") == MacProgramCheck.Missing, "a file as a directory");
        Check(Trust("relative") == MacProgramCheck.Missing, "relative");
        acls.Add("/usr/bin");
        Check(Trust("/usr/bin/tool") == MacProgramCheck.NeedsSignature, "an ACL that lets another user change the directory");
        acls.Clear();
        acls.Add("/usr/bin/tool");
        Check(Trust("/usr/bin/tool") == MacProgramCheck.NeedsSignature, "an ACL that lets another user change the program");
    }

    [Test, DisplayName("program: a .NET application host beside its assemblies is not NativeAOT")]
    public void ManagedPrograms()
    {
        var files = new HashSet<string> { "/opt/a/tool.runtimeconfig.json", "/opt/b/tool.deps.json", "/opt/c/tool.dll", "/opt/d/other.dll" };
        foreach (var directory in new[] { "a", "b", "c" })
            Check(MacManagedProgram.IsManaged($"/opt/{directory}/tool", files.Contains), directory);
        Check(!MacManagedProgram.IsManaged("/opt/d/tool", files.Contains), "another program's assembly");
    }

    [Test, DisplayName("client: the hardened runtime is required; a debugged process and get-task-allow are refused")]
    public void HardenedClients()
    {
        const long Runtime = 0x10000, GetTaskAllow = 0x4, Debugged = 0x10000000, Valid = 0x1;
        HashSet<string> none = [];
        Check(MacPrivilegedOutcome.IsHardenedClient(Valid | Runtime, none, false), "hardened");
        Check(!MacPrivilegedOutcome.IsHardenedClient(Valid, none, false), "not hardened");
        Check(!MacPrivilegedOutcome.IsHardenedClient(Valid | Runtime | Debugged, none, true), "debugged");
        Check(!MacPrivilegedOutcome.IsHardenedClient(Valid | Runtime | GetTaskAllow, none, false), "get-task-allow");
        Check(MacPrivilegedOutcome.IsHardenedClient(Valid | Runtime | GetTaskAllow, none, true), "get-task-allow with development signing");
        foreach (var entitlement in new[] { "allow-dyld-environment-variables", "disable-library-validation", "allow-jit", "allow-unsigned-executable-memory" })
        {
            HashSet<string> one = ["com.apple.security.cs." + entitlement];
            Check(!MacPrivilegedOutcome.IsHardenedClient(Valid | Runtime, one, false), entitlement);
            Check(MacPrivilegedOutcome.IsHardenedClient(Valid | Runtime, one, true), entitlement + " with development signing");
        }
        Check(MacPrivilegedOutcome.IsHardenedClient(Valid | Runtime, new HashSet<string> { "com.apple.security.app-sandbox" }, false), "other entitlements");
    }

    [Test, DisplayName("program: the hardened runtime, and no entitlement for unsigned libraries or generated code")]
    public void HardenedPrograms()
    {
        const long Runtime = 0x10000, LibraryValidation = 0x2000;
        Check(MacPrivilegedOutcome.IsHardenedProgram(Runtime, new HashSet<string>()), "hardened");
        Check(!MacPrivilegedOutcome.IsHardenedProgram(LibraryValidation, new HashSet<string>()), "library validation alone is not the hardened runtime");
        foreach (var entitlement in new[] { "disable-library-validation", "allow-jit", "allow-unsigned-executable-memory", "allow-dyld-environment-variables" })
            Check(!MacPrivilegedOutcome.IsHardenedProgram(Runtime, new HashSet<string> { "com.apple.security.cs." + entitlement }), entitlement);
    }

    [Test, DisplayName("program: the .NET host's bundle marker and libhostfxr are found anywhere in the file")]
    public void HostMarkers()
    {
        // The bundle marker of dotnet/runtime's bundle_marker.c, from its complement so this assembly does not carry it.
        byte[] marker = [.. new byte[] { 0x74, 0xed, 0xfd, 0x46, 0x95, 0x9e, 0xdf, 0xc7, 0x8d, 0x84, 0x6c, 0xfd, 0xeb, 0x28, 0x5f, 0xcd,
            0xec, 0x0a, 0x46, 0x19, 0x10, 0x51, 0xcc, 0xe7, 0x11, 0xc4, 0xd2, 0x31, 0xdb, 0x4c, 0x95, 0x51 }.Select(b => (byte)~b)];
        foreach (var offset in new[] { 0, 100, 64 * 1024 - 10, 200_000 })
        {
            var bytes = new byte[offset + 300_000];
            marker.CopyTo(bytes, offset);
            Check(MacManagedProgram.HasHostMarker(new MemoryStream(bytes)), $"marker at {offset}");
        }
        var host = new byte[100_000];
        // Joined at run time: the NativeAOT test executable is also the root test's daemon, which scans its own image.
        System.Text.Encoding.ASCII.GetBytes(string.Join("", "lib", "host", "fxr", ".dylib")).CopyTo(host, 64 * 1024 - 4);
        Check(MacManagedProgram.HasHostMarker(new MemoryStream(host)), "libhostfxr across two reads");
        Check(!MacManagedProgram.HasHostMarker(new MemoryStream(new byte[300_000])), "no marker");
        Check(!MacManagedProgram.HasHostMarker(new MemoryStream(host), hostLibrary: false), "the daemon's own check looks for the bundle marker only");
    }

    [Test, DisplayName("output: lines, CRLF, long lines split between UTF-8 sequences, and a last unterminated line")]
    public void LineReader()
    {
        var lines = new List<byte[]>();
        var text = "one\r\ntwo\n\nthree";
        MacLineReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(text)), lines.Add);
        Check(lines.Select(l => Encoding.UTF8.GetString(l)).SequenceEqual(["one", "two", "", "three"]), "lines");

        lines.Clear();
        var longLine = "a" + string.Concat(Enumerable.Repeat("é", MacLineReader.MaximumLineBytes)); // 2-byte sequences after one byte
        MacLineReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(longLine + "\n")), lines.Add);
        Check(lines.Count > 1 && lines.All(l => l.Length <= MacLineReader.MaximumLineBytes), "parts");
        Check(string.Concat(lines.Select(l => Encoding.UTF8.GetString(l, 0, l.Length))) == longLine, "no character is cut in two");
    }

    [Test, DisplayName("environment: fixed variables, the caller's user ID and checked locale variables only")]
    public void ProgramEnvironment()
    {
        var environment = MacPrivilegedProtocol.ProgramEnvironment(501, new Dictionary<string, string>
        {
            ["LANG"] = "de_DE.UTF-8",
            ["LC_ALL"] = "bad value; rm -rf /",
            ["LC_TIME"] = "",
            ["RUNIC_SECRET"] = "secret",
        });
        Check(environment.SequenceEqual(["PATH=/usr/bin:/bin:/usr/sbin:/sbin", "HOME=/var/root", "USER=root", "LOGNAME=root", "RUNIC_CALLER_UID=501", "LANG=de_DE.UTF-8"]),
            string.Join(", ", environment));
    }

    [Test, DisplayName("daemon: undeclared actions and arguments outside the declaration are rejected before anything else")]
    public void DaemonValidation()
    {
        var authority = new FakeAuthority();
        var server = new MacPrivilegedDaemonServer([Shell], Definition(Shell), authority, null);
        var session = new MacDaemonSession(server, 0);
        Check(server.Execute(Request("eu.runic.tests.other", []), 501, session).Status == MacReplyStatus.Rejected, "undeclared");
        Check(server.Execute(Request(Shell.Id, ["a", "b", "c", "d"]), 501, session).Status == MacReplyStatus.Rejected, "too many arguments");
        Check(server.Execute(Request(Shell.Id, ["a\0b"]), 501, session).Status == MacReplyStatus.Rejected, "NUL");
        Check(authority.Checks == 0 && authority.Ensured == 0, "no authorization was checked for an invalid request");
        Check(server.Validate(Request(Shell.Id, ["exit 3"]), out var action, out var argv).Status == MacReplyStatus.Ready && action == Shell && argv.SequenceEqual(["-c", "exit 3"]), "the verb comes first");

        authority.CheckStatus = MacPrivilegedOutcome.AuthorizationDenied;
        Check(server.Execute(Request(Shell.Id, ["true"]), 501, session) == new MacReply(MacReplyStatus.Denied, -60005), "the daemon's check of the right decides");
        authority.EnsureStatus = -60008;
        Check(server.Execute(Request(Shell.Id, ["true"]), 501, session) == new MacReply(MacReplyStatus.Failed, -60008), "a right that cannot be defined");
        Check(server.Prepare() == new MacReply(MacReplyStatus.Failed, -60008), "prepare reports it too");

        for (var i = 0; i < MacPrivilegedDaemonServer.RunsPerClient; i++) Check(server.TryBeginRun(10), $"run {i} of a client");
        Check(!server.TryBeginRun(10), "a client's limit");
        for (var client = 11; client < 11 + MacPrivilegedDaemonServer.RunsInTotal - MacPrivilegedDaemonServer.RunsPerClient; client++) Check(server.TryBeginRun(client), $"client {client}");
        Check(!server.TryBeginRun(99), "the total limit");
        server.EndRun(10);
        Check(server.TryBeginRun(99), "a finished run frees a place");

        Throws<ArgumentException>(() => _ = new MacPrivilegedDaemonServer([Shell, Id], Definition(Shell), authority, null), "an action without a right");
        Throws<ArgumentException>(() => _ = new MacPrivilegedDaemonServer([Shell], Definition(Shell, Id), authority, null), "a right without an action");
    }

    [Test, DisplayName("provider: requests outside the declarations throw; an unavailable host runs nothing")]
    public async Task ProviderValidationAsync()
    {
        var host = new FakeHost();
        var provider = new MacPrivilegedProvider([Shell], Definition(Shell), host);
        await ThrowsAsync<ArgumentException>(() => provider.RunAsync(new(Id, []), default).AsTask(), "undeclared");
        await ThrowsAsync<ArgumentException>(() => provider.RunAsync(new(new PrivilegedActionDeclaration(Shell.Id, "/bin/zsh") { Verb = "-c", MaximumArguments = 3 }, []), default).AsTask(), "another declaration with the same ID");
        await ThrowsAsync<ArgumentException>(() => provider.RunAsync(new(Shell, ["1", "2", "3", "4"]), default).AsTask(), "too many");
        Throws<ArgumentException>(() => _ = new MacPrivilegedProvider([Shell, Id], Definition(Shell), host), "an action without a right");
        host.Unavailable = PlatformUnavailableReason.BackendUnavailable;
        Check(Describe(await provider.RunAsync(new(Shell, ["true"]))) == "Unavailable:BackendUnavailable", "unavailable");
        Check(host.Connections == 0 && host.Authorizations == 0, "nothing connected or prompted");
        if (!OperatingSystem.IsMacOS())
            Check(Describe(await MacPrivilegedActions.Create([Shell], Definition(Shell)).RunAsync(new(Shell, ["true"]))) == "Unavailable:BackendUnavailable", "off macOS");
    }

    [Test, DisplayName("provider: the daemon prepares the rights before the user is asked")]
    public async Task ProviderPrepareAsync()
    {
        var host = new FakeHost();
        var provider = new MacPrivilegedProvider([Shell], Definition(Shell), host);
        host.Channel.PrepareReply = new(MacReplyStatus.Unreachable);
        Check(Describe(await provider.RunAsync(new(Shell, ["true"]))) == "Unavailable:BackendUnavailable", "no daemon");
        host.Channel.PrepareReply = new(MacReplyStatus.UntrustedDaemon);
        Check(Describe(await provider.RunAsync(new(Shell, ["true"]))) == "Failed:InvalidData", "a service that is not the daemon");
        host.Channel.PrepareReply = new(MacReplyStatus.Interrupted);
        Check(Describe(await provider.RunAsync(new(Shell, ["true"]))) == "Unavailable:BackendUnavailable", "a daemon that refused or left before anything ran");
        host.Channel.PrepareReply = new(MacReplyStatus.Failed, -60005);
        Check(Describe(await provider.RunAsync(new(Shell, ["true"]))) == "Failed:Other", "the rights could not be defined");
        Check(host.Authorizations == 0 && host.Channel.Disposals == 4, "never asked; every connection closed");
    }

    [Test, DisplayName("provider: dismissed and denied authorization, with the rights destroyed")]
    public async Task ProviderAuthorizationAsync()
    {
        var host = new FakeHost();
        var provider = new MacPrivilegedProvider([Shell], Definition(Shell), host);
        host.AuthorizationStatus = MacPrivilegedOutcome.AuthorizationCanceled;
        Check(Describe(await provider.RunAsync(new(Shell, ["true"]))) == "Failed:UserDismissed", "dismissed");
        host.AuthorizationStatus = MacPrivilegedOutcome.AuthorizationDenied;
        Check(Describe(await provider.RunAsync(new(Shell, ["true"]))) == "Failed:PermissionDenied", "denied");
        host.AuthorizationStatus = MacPrivilegedOutcome.AuthorizationInteractionNotAllowed;
        Check(Describe(await provider.RunAsync(new(Shell, ["true"]))) == "Unavailable:BackendUnavailable", "no dialog possible");
        Check(host.Destroyed == 3 && host.Channel.Runs == 0, "no run; every authorization destroyed");
        Check(host.AuthorizedRights.All(r => r == Shell.Id), "the right is the action ID");
    }

    [Test, DisplayName("provider: a run sends the argument vector and the external form, and delivers decoded output")]
    public async Task ProviderRunAsync()
    {
        var host = new FakeHost();
        var provider = new MacPrivilegedProvider([Shell], Definition(Shell), host);
        host.Channel.Run = (request, output) =>
        {
            output(PrivilegedOutputChannel.StandardOutput, Encoding.UTF8.GetBytes("out é"));
            output(PrivilegedOutputChannel.StandardError, [0x66, 0xFF]);
            return Task.FromResult(new MacReply(MacReplyStatus.Ran, 3));
        };
        var lines = new List<PrivilegedActionOutput>();
        var result = await provider.RunAsync(new(Shell, ["printf '%s' \"$0\"", "a b; $(id)"]) { Output = lines.Add });
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 3 }, Describe(result));
        Check(lines.SequenceEqual([new(PrivilegedOutputChannel.StandardOutput, "out é"), new(PrivilegedOutputChannel.StandardError, "f�")]), "output");
        var sent = host.Channel.Requests.Single();
        Check(sent.ActionId == Shell.Id && sent.Arguments.SequenceEqual(["printf '%s' \"$0\"", "a b; $(id)"]), "the arguments after the verb, verbatim");
        Check(sent.Authorization.SequenceEqual(FakeHost.Form), "the external form");
        Check(host.Destroyed == 1, "the rights are destroyed after the run");
    }

    [Test, DisplayName("provider: cancellation before the authorization answers, before the start and while running")]
    public async Task ProviderCancellationAsync()
    {
        var host = new FakeHost();
        var provider = new MacPrivilegedProvider([Shell], Definition(Shell), host);

        // The dialog cannot be withdrawn: the run stops waiting, and the rights are destroyed once the user answers.
        host.Gate = new SemaphoreSlim(0);
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            await ThrowsAsync<OperationCanceledException>(() => provider.RunAsync(new(Shell, ["true"]), cancel.Token).AsTask(), "cancelled while the dialog is open");
        Check(host.Destroyed == 0, "still open");
        host.Gate.Release();
        await WaitUntilAsync(() => host.Destroyed == 1, "destroyed after the answer");
        host.Gate = null;
        Check(host.Channel.Runs == 0, "nothing ran");

        // The daemon reports a cancellation before the program started.
        host.Channel.Run = (_, _) => Task.FromResult(new MacReply(MacReplyStatus.Cancelled));
        await ThrowsAsync<OperationCanceledException>(() => provider.RunAsync(new(Shell, ["true"])).AsTask(), "cancelled before the start");

        // A running program: the cancellation reaches the daemon, and the result is how the program ended.
        var finished = new TaskCompletionSource<MacReply>();
        host.Channel.Run = (_, _) => finished.Task;
        host.Channel.CancelRequested = () => finished.TrySetResult(new(MacReplyStatus.Ran, 0));
        using (var cancel = new CancellationTokenSource())
        {
            var run = provider.RunAsync(new(Shell, ["cat"]), cancel.Token).AsTask();
            await WaitUntilAsync(() => host.Channel.Runs == 2, "running");
            cancel.Cancel();
            Check(await run is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 0 }, "the program ended on its own terms");
        }
        Check(host.Channel.Cancels == 1, "one cancellation request");
    }

    [Test, DisplayName("provider: an output callback's exception is thrown after the run ends")]
    public async Task ProviderCallbackFailureAsync()
    {
        var host = new FakeHost();
        var provider = new MacPrivilegedProvider([Shell], Definition(Shell), host);
        var calls = 0;
        host.Channel.Run = (_, output) =>
        {
            output(PrivilegedOutputChannel.StandardOutput, "a"u8.ToArray());
            output(PrivilegedOutputChannel.StandardOutput, "b"u8.ToArray());
            return Task.FromResult(new MacReply(MacReplyStatus.Ran, 0));
        };
        await ThrowsAsync<InvalidTimeZoneException>(() => provider.RunAsync(new(Shell, ["true"]) { Output = _ => { calls++; throw new InvalidTimeZoneException(); } }).AsTask(), "rethrown");
        Check(calls == 1, "no further lines after a failure");
    }

    internal static MacRunRequest Request(string id, ImmutableArray<string> arguments) =>
        new(id, arguments, new byte[32], new Dictionary<string, string>());

    internal static string Describe<T>(PlatformResult<T> result) => result switch
    {
        PlatformResult<T>.Success success => $"Success:{success.Value}",
        PlatformResult<T>.Failed failed => $"Failed:{failed.Code}",
        PlatformResult<T>.Unavailable unavailable => $"Unavailable:{unavailable.Reason}",
        _ => result.ToString() ?? "",
    };

    internal static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}: {message}");
    }

    internal static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}: {message}");
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Check(condition(), message);
    }

    private sealed class FakeAuthority : IMacDaemonAuthority
    {
        public int EnsureStatus, CheckStatus, Ensured, Checks;
        public int EnsureRight(MacPrivilegedRight right) { Ensured++; return EnsureStatus; }
        public int Check(string name, byte[] externalForm) { Checks++; return CheckStatus; }
    }

    private sealed class FakeHost : MacPrivilegedHost
    {
        internal static readonly byte[] Form = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
        public PlatformUnavailableReason? Unavailable;
        public int AuthorizationStatus, Connections, Authorizations, Destroyed;
        public SemaphoreSlim? Gate;
        public List<string> AuthorizedRights { get; } = [];
        public FakeChannel Channel { get; } = new();

        internal override PlatformUnavailableReason? CheckAvailable(MacPrivilegedDaemonDefinition daemon) => Unavailable;
        internal override IMacPrivilegedChannel Connect(MacPrivilegedDaemonDefinition daemon) { Connections++; return Channel; }

        internal override MacAuthorizationGrant Authorize(string right)
        {
            Authorizations++;
            AuthorizedRights.Add(right);
            Gate?.Wait();
            return new MacAuthorizationGrant(AuthorizationStatus, AuthorizationStatus == 0 ? Form : null, () => Interlocked.Increment(ref Destroyed));
        }

        internal override IReadOnlyDictionary<string, string> PassedEnvironment() => new Dictionary<string, string>();
    }

    internal sealed class FakeChannel : IMacPrivilegedChannel
    {
        public MacReply PrepareReply = new(MacReplyStatus.Ready);
        public Func<MacRunRequest, Action<PrivilegedOutputChannel, byte[]>, Task<MacReply>> Run = (_, _) => Task.FromResult(new MacReply(MacReplyStatus.Ran, 0));
        public Action? CancelRequested;
        public int Runs, Cancels, Disposals;
        public List<MacRunRequest> Requests { get; } = [];

        public Task<MacReply> PrepareAsync() => Task.FromResult(PrepareReply);

        public Task<MacReply> RunAsync(MacRunRequest request, Action<PrivilegedOutputChannel, byte[]> output)
        {
            Runs++;
            Requests.Add(request);
            return Run(request, output);
        }

        public void RequestCancel()
        {
            Cancels++;
            CancelRequested?.Invoke();
        }

        public void Dispose() => Disposals++;
    }
}
