using System.Collections.Immutable;
using TUnit.Core;
using static Runic.Platform.MacOS.Tests.PrivilegedModelTests;

namespace Runic.Platform.MacOS.Tests;

/// <summary>
/// The XPC protocol, code-signing checks, posix_spawn and Authorization Services on macOS, with the daemon served in
/// this process from an anonymous XPC listener, as the user running the tests. Select with
/// <c>--treenode-filter "/*/*/*/*[Category=NativePrivileged]"</c>. PrivilegedRootTests installs the real daemon.
/// </summary>
internal sealed class PrivilegedNativeTests
{
    private static readonly PrivilegedActionDeclaration Env = new("eu.runic.tests.env", "/usr/bin/env");
    private static readonly PrivilegedActionDeclaration Pwd = new("eu.runic.tests.pwd", "/bin/pwd");

    [Test, Explicit, Category("NativePrivileged"), DisplayName("native: the argument vector, output streams and exit code over XPC")]
    public async Task RunsAsync()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var daemon = new InProcessDaemon([Shell, Env, Pwd]);
        var lines = new List<PrivilegedActionOutput>();
        var result = await daemon.Provider.RunAsync(new(Shell, ["printf '%s\\n' \"$0\" \"$1\"; echo err >&2; exit 3", "a b; $(id)", "`id` 'q' \"r\""]) { Output = lines.Add });
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 3 }, Describe(result));
        Check(lines.Where(l => l.Channel == PrivilegedOutputChannel.StandardOutput).Select(l => l.Text).SequenceEqual(["a b; $(id)", "`id` 'q' \"r\""]), Dump(lines));
        Check(lines.Where(l => l.Channel == PrivilegedOutputChannel.StandardError).Select(l => l.Text).SequenceEqual(["err"]), Dump(lines));

        lines.Clear();
        Environment.SetEnvironmentVariable("RUNIC_TEST_SECRET", "secret");
        result = await daemon.Provider.RunAsync(new(Env, []) { Output = lines.Add });
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 0 }, Describe(result));
        var expected = new[] { "PATH=/usr/bin:/bin:/usr/sbin:/sbin", "HOME=/var/root", "USER=root", "LOGNAME=root", $"RUNIC_CALLER_UID={MacProcess.GetEffectiveUserId()}", "LANG=en_US.UTF-8" };
        Check(lines.Select(l => l.Text).Order().SequenceEqual(expected.Order()), "exactly the daemon's environment: " + Dump(lines));

        lines.Clear();
        result = await daemon.Provider.RunAsync(new(Pwd, []) { Output = lines.Add });
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 0 } && lines.Single().Text == "/", "the working directory is /: " + Dump(lines));
    }

    [Test, Explicit, Category("NativePrivileged"), DisplayName("native: the program waits while the application takes a line")]
    public async Task BackpressureAsync()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var daemon = new InProcessDaemon([Shell]);
        var marker = Path.Combine(Path.GetTempPath(), "runic-privileged-" + Guid.NewGuid().ToString("N"));
        using var first = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        long characters = 0;
        var parts = 0;
        var run = daemon.Provider.RunAsync(new(Shell, ["head -c 1048576 /dev/zero | tr '\\0' a; echo; : > \"$0\"", marker])
        {
            Output = line =>
            {
                if (parts++ == 0)
                {
                    first.Release();
                    release.Wait();
                }
                characters += line.Text.Length;
            },
        }).AsTask();
        Check(await first.WaitAsync(TestTimeouts.Scale(TimeSpan.FromSeconds(30))), "first line");
        await Task.Delay(1000);
        Check(!File.Exists(marker), "1 MiB of output does not fit the pipes: the program must still be writing");
        release.Release();
        var result = await run;
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 0 }, Describe(result));
        Check(characters == 1048576 && parts >= 64 && File.Exists(marker), $"{characters} characters in {parts} parts");
        File.Delete(marker);
    }

    [Test, Explicit, Category("NativePrivileged"), DisplayName("native: cancellation closes the running program's input, or stops it before it starts")]
    public async Task CancellationAsync()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var daemon = new InProcessDaemon([Shell]);
        var lines = new List<string>();
        using (var cancel = new CancellationTokenSource())
        {
            var result = await daemon.Provider.RunAsync(new(Shell, ["echo started; cat; echo after-eof"])
            {
                Output = line =>
                {
                    lines.Add(line.Text);
                    if (line.Text == "started") cancel.Cancel();
                },
            }, cancel.Token);
            Check(result is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 0 }, Describe(result));
            Check(lines.SequenceEqual(["started", "after-eof"]), "standard input reached its end: " + string.Join(",", lines));
        }

        var marker = Path.Combine(Path.GetTempPath(), "runic-privileged-" + Guid.NewGuid().ToString("N"));
        daemon.Authority.Gate = new SemaphoreSlim(0);
        using (var cancel = new CancellationTokenSource())
        {
            var run = daemon.Provider.RunAsync(new(Shell, [": > \"$0\"", marker]), cancel.Token).AsTask();
            await WaitUntilAsync(() => daemon.Authority.Checks == 1, "the daemon checks the authorization");
            cancel.Cancel();
            await Task.Delay(200);
            daemon.Authority.Gate.Release();
            await ThrowsAsync<OperationCanceledException>(() => run, "cancelled before the start");
            await Task.Delay(500);
            Check(!File.Exists(marker), "the program never ran");
        }
    }

    [Test, Explicit, Category("NativePrivileged"), DisplayName("native: a program that users can change runs only signed with its identifier and the hardened runtime")]
    public async Task ProgramSignatureAsync()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var directory = Directory.CreateTempSubdirectory("runic-privileged-");
        try
        {
            // Programs in the user's temporary directory: their path is the user's, so their signature decides.
            var source = Path.Combine(directory.FullName, "echo.c");
            File.WriteAllText(source, "#include <stdio.h>\nint main(int c, char **v) { for (int i = 1; i < c; i++) puts(v[i]); return 0; }\n");
            // A program that carries the .NET host's bundle marker, as an apphost or a single-file bundle does,
            // written from its complement so this executable, the root test's daemon, does not carry it.
            byte[] marker = [.. new byte[] { 0x74, 0xed, 0xfd, 0x46, 0x95, 0x9e, 0xdf, 0xc7, 0x8d, 0x84, 0x6c, 0xfd, 0xeb, 0x28, 0x5f, 0xcd,
                0xec, 0x0a, 0x46, 0x19, 0x10, 0x51, 0xcc, 0xe7, 0x11, 0xc4, 0xd2, 0x31, 0xdb, 0x4c, 0x95, 0x51 }.Select(b => (byte)~b)];
            var markedSource = Path.Combine(directory.FullName, "marked.c");
            File.WriteAllText(markedSource, "#include <stdio.h>\nvolatile const unsigned char marker[] = {" + string.Join(",", marker.Select(b => $"0x{b:x2}"))
                + "};\nint main(void) { return marker[0] == 0; }\n");
            string Build(string name, bool hardened, string? entitlement = null, string? from = null)
            {
                var path = Path.Combine(directory.FullName, name);
                Tool("/usr/bin/cc", "-o", path, from ?? source);
                List<string> arguments = ["--force", "--sign", "-", "--identifier", "eu.runic.tests.echo"];
                if (hardened) arguments.Add("--options=runtime");
                if (entitlement is not null)
                {
                    var plist = path + ".entitlements";
                    File.WriteAllText(plist, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>{entitlement}</key><true/></dict></plist>");
                    arguments.AddRange(["--entitlements", plist]);
                }
                arguments.Add(path);
                Tool("/usr/bin/codesign", [.. arguments]);
                return path;
            }
            var hardened = Build("hardened", true);
            var plain = Build("plain", false);
            var managed = Build("managed", true);
            File.WriteAllText(managed + ".runtimeconfig.json", "{}");
            var unvalidated = Build("unvalidated", true, "com.apple.security.cs.disable-library-validation");
            var jit = Build("jit", true, "com.apple.security.cs.allow-jit");
            var marked = Build("marked", true, from: markedSource);
            var script = Path.Combine(directory.FullName, "script");
            File.WriteAllText(script, "#!/bin/sh\necho unsigned\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            PrivilegedActionDeclaration Declare(string id, string program) => new($"eu.runic.tests.{id}", program) { MaximumArguments = 1 };
            var signed = Declare("signed", hardened);
            var notHardened = Declare("not-hardened", plain);
            var dotnet = Declare("managed", managed);
            var unnamed = Declare("unnamed", hardened);
            var libraries = Declare("libraries", unvalidated);
            var generated = Declare("generated", jit);
            var bundled = Declare("bundled", marked);
            var renamed = Declare("renamed", hardened);
            var unsigned = Declare("script", script);
            var missing = Declare("missing", Path.Combine(directory.FullName, "missing"));
            var identifiers = new Dictionary<string, string>
            {
                [signed.Id] = "eu.runic.tests.echo", [notHardened.Id] = "eu.runic.tests.echo", [dotnet.Id] = "eu.runic.tests.echo",
                [renamed.Id] = "eu.runic.tests.other", [unsigned.Id] = "eu.runic.tests.echo", [missing.Id] = "eu.runic.tests.echo",
                [libraries.Id] = "eu.runic.tests.echo", [generated.Id] = "eu.runic.tests.echo", [bundled.Id] = "eu.runic.tests.echo",
            };

            // The ad hoc signatures have no certificate; the requirement here is only the identifier the daemon adds.
            using var daemon = new InProcessDaemon([signed, notHardened, dotnet, unnamed, renamed, unsigned, missing, libraries, generated, bundled],
                programRequirement: "always", identifiers: identifiers);
            var lines = new List<PrivilegedActionOutput>();
            var result = await daemon.Provider.RunAsync(new(signed, ["signed"]) { Output = lines.Add });
            Check(result is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 0 } && lines.Single().Text == "signed",
                $"checked on disk and in the suspended process: {Describe(result)} {Dump(lines)}");
            foreach (var (action, code) in new[] { (notHardened, 10L), (dotnet, 8L), (unnamed, 9L), (renamed, 3L), (unsigned, 3L), (missing, 1L),
                (libraries, 10L), (generated, 10L), (bundled, 8L) })
            {
                result = await daemon.Provider.RunAsync(new(action, []));
                Check(result is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.InvalidData } failed && failed.Diagnostic!.Code == code,
                    $"{action.Id}: {Describe(result)} {(result as PlatformResult<PrivilegedActionCompletion>.Failed)?.Diagnostic}");
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    private static void Tool(string file, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Check(process.ExitCode == 0, $"{file} {string.Join(' ', arguments)}: {output.Result}{error}");
    }

    [Test, Explicit, Category("NativePrivileged"), DisplayName("native: both sides check the other's code signature; the daemon checks its own declarations")]
    public async Task PeersAndDeclarationsAsync()
    {
        if (!OperatingSystem.IsMacOS()) return;
        const string Other = "identifier \"eu.runic.tests.nothing\" and anchor apple generic";
        using (var daemon = new InProcessDaemon([Shell], daemonRequirement: Other))
        {
            var result = await daemon.Provider.RunAsync(new(Shell, ["true"]));
            Console.WriteLine($"daemon not signed as required: {Describe(result)}");
            // libxpc reports the failed requirement (InvalidData), or only invalidates the connection (Unavailable).
            Check(result is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.InvalidData, Diagnostic.Code: 5 }
                or PlatformResult<PrivilegedActionCompletion>.Unavailable, "the daemon is not signed as required: " + Describe(result));
            // libxpc checks the daemon's requirement on what comes back, so the prepare message reaches the service,
            // but the client stops at its reply: no authorization and no external form is ever sent.
            Check(daemon.Authority.Checks == 0, "no run reached the service");
        }
        using (var daemon = new InProcessDaemon([Shell], applicationRequirement: Other))
        {
            var result = await daemon.Provider.RunAsync(new(Shell, ["true"]));
            Check(result is PlatformResult<PrivilegedActionCompletion>.Unavailable, "the application is not signed as required: " + Describe(result));
            Check(daemon.Authority.Checks == 0 && daemon.Authority.Ensured == 0, "the daemon served nothing");
        }
        using (var daemon = new InProcessDaemon([Shell], clientActions: [Shell, Id]))
        {
            var result = await daemon.Provider.RunAsync(new(Id, []));
            Check(result is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.InvalidData, Diagnostic.Code: 4 }, "the daemon does not declare it: " + Describe(result));
        }
        using (var daemon = new InProcessDaemon([Shell]))
        {
            daemon.Authority.CheckStatus = MacPrivilegedOutcome.AuthorizationDenied;
            var result = await daemon.Provider.RunAsync(new(Shell, ["true"]));
            Check(result is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.PermissionDenied, Diagnostic.Code: -60005 }, "denied by the daemon: " + Describe(result));
        }
    }

    /// <summary>
    /// Whether this test process runs as the daemon requires of a client: the hardened runtime, not debugged, no
    /// get-task-allow. CI re-signs the NativeAOT executable with <c>--options=runtime</c>; the dotnet host that runs the
    /// JIT build is not such a client, and the daemon refuses it.
    /// </summary>
    internal static bool SelfHardened()
    {
        if (MacSecurity.CodeCopySelf(0, out var code) != 0) return false;
        try { return MacSecurity.DynamicSigning(code) is { } signing && MacPrivilegedOutcome.IsHardenedClient(signing.Status, signing.Entitlements, false); }
        finally { MacSecurity.Release(code); }
    }

    [Test, Explicit, Category("NativePrivileged"), DisplayName("native: the daemon refuses a client without the hardened runtime")]
    public async Task HardenedClientAsync()
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (MacSecurity.CodeCopySelf(0, out var code) == 0)
        {
            Console.WriteLine($"this process's code-signing status: 0x{MacSecurity.DynamicStatus(code):x}");
            MacSecurity.Release(code);
        }
        if (SelfHardened())
        {
            Check(!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported, "only the re-signed NativeAOT executable is a hardened client");
            Console.WriteLine("hardened: the other cases run the daemon's client checks against this process");
            return;
        }
        Check(System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
            "the NativeAOT executable must be signed with the hardened runtime (codesign --force --sign - --options=runtime)");
        using var daemon = new InProcessDaemon([Shell], checkSender: true);
        var result = await daemon.Provider.RunAsync(new(Shell, ["true"]));
        Check(result is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.InvalidData, Diagnostic.Code: 4 } && daemon.Authority.Checks == 0,
            "a client without the hardened runtime: " + Describe(result));
    }

    [Test, Explicit, Category("NativePrivileged"), DisplayName("native: Authorization Services calls that need no dialog")]
    public unsafe void AuthorizationServices()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // An external form round trip.
        Check(MacSecurity.AuthorizationCreate(null, null, 0, out var authorization) == 0, "AuthorizationCreate");
        var form = new byte[MacSecurity.ExternalFormLength];
        fixed (byte* bytes = form) Check(MacSecurity.AuthorizationMakeExternalForm(authorization, bytes) == 0, "AuthorizationMakeExternalForm");
        fixed (byte* bytes = form) Check(MacSecurity.AuthorizationCreateFromExternalForm(bytes, out var copy) == 0 && MacSecurity.AuthorizationFree(copy, 0) == 0, "AuthorizationCreateFromExternalForm");
        // The daemon's check of an authorization that holds no credential fails without asking.
        var denied = MacSystemAuthority.Instance.Check("system.privilege.admin", form);
        Check(denied is MacPrivilegedOutcome.AuthorizationDenied or MacPrivilegedOutcome.AuthorizationInteractionNotAllowed, $"daemon check without a credential: {denied}");
        Check(MacSecurity.AuthorizationFree(authorization, MacSecurity.FlagDestroyRights) == 0, "AuthorizationFree");
        Check(MacSystemAuthority.Instance.Check("system.privilege.admin", new byte[5]) == MacPrivilegedOutcome.AuthorizationDenied, "a malformed form");

        // The application's request without interaction, as in a session without a dialog.
        using (var grant = MacPrivilegedSystemHost.AuthorizeWithFlags("system.privilege.admin", MacSecurity.FlagExtendRights))
        {
            Check(grant.Status is MacPrivilegedOutcome.AuthorizationInteractionNotAllowed or MacPrivilegedOutcome.AuthorizationDenied && grant.ExternalForm is null, $"no interaction: {grant.Status}");
            Console.WriteLine($"AuthorizationCopyRights without interaction: {grant.Status}");
        }

        // Reading a right's definition, and the comparison the daemon makes before it redefines a right.
        var admin = MacSecurity.CopyRight("system.privilege.admin");
        Check(admin != 0, "AuthorizationRightGet");
        try
        {
            Check(MacSecurity.ToText(MacSecurity.Get(admin, "class")) == "user" && MacSecurity.ToText(MacSecurity.Get(admin, "group")) == "admin", "class and group");
            Check(MacSecurity.ToBoolean(MacSecurity.Get(admin, "shared")) == false && MacSecurity.ToNumber(MacSecurity.Get(admin, "timeout")) is > 0, "shared and timeout");
        }
        finally { MacSecurity.Release(admin); }
        var right = new MacPrivilegedRight("eu.runic.tests.rule", "Runic tests want to compare a rule.");
        var definition = MacSystemAuthority.CreateDefinition(right);
        try
        {
            Check(MacSystemAuthority.Matches(definition, right), "the daemon's own definition, with its prompt, matches");
            Check(!MacSystemAuthority.Matches(definition, new MacPrivilegedRight(right.ActionId, "Another message.")), "another message");
            MacSecurity.Set(definition, "created", MacSecurity.CreateNumber(1));
            MacSecurity.Set(definition, "identifier", MacSecurity.CreateString("eu.runic.tests"));
            Check(MacSystemAuthority.Matches(definition, right), "authd's metadata is tolerated");
            MacSecurity.Set(definition, "timeout", MacSecurity.CreateNumber(0));
            Check(!MacSystemAuthority.Matches(definition, right), "another timeout");
        }
        finally { MacSecurity.Release(definition); }

        // Any key the rule does not have can change how the right is evaluated or shown: the daemon replaces it.
        foreach (var (key, value) in new (string, Func<nint>)[]
        {
            ("mechanisms", () => MacSecurity.CreateString("builtin:authenticate")),
            ("extract-password", () => MacSecurity.Retain(MacSecurity.True)),
            ("entitled", () => MacSecurity.Retain(MacSecurity.True)),
            ("default-button", () => MacSecurity.CreateDictionary()),
        })
        {
            var extended = MatchingDefinition(right);
            try
            {
                MacSecurity.Set(extended, key, value());
                Check(!MacSystemAuthority.Matches(extended, right), $"an extra '{key}'");
            }
            finally { MacSecurity.Release(extended); }
        }
        var localized = MatchingDefinition(right);
        try
        {
            var prompts = MacSecurity.CreateDictionary();
            MacSecurity.Set(prompts, "", MacSecurity.CreateString(right.Message));
            MacSecurity.Set(prompts, "de", MacSecurity.CreateString("Ein anderer Text."));
            MacSecurity.Set(localized, "default-prompt", prompts);
            Check(!MacSystemAuthority.Matches(localized, right), "a localized prompt");
        }
        finally { MacSecurity.Release(localized); }
    }

    private static nint MatchingDefinition(MacPrivilegedRight right)
    {
        var definition = MacSystemAuthority.CreateDefinition(right);
        Check(MacSystemAuthority.Matches(definition, right), "the daemon's own definition matches");
        return definition;
    }

    /// <summary>The daemon's server on an anonymous listener in this process, and a provider that reaches it through its endpoint.</summary>
    internal sealed class InProcessDaemon : IDisposable
    {
        private readonly nint _queue, _listener, _endpoint;

        public InProcessDaemon(PrivilegedActionDeclaration[] actions, string? programRequirement = null, string? daemonRequirement = null,
            string? applicationRequirement = null, PrivilegedActionDeclaration[]? clientActions = null, IReadOnlyDictionary<string, string>? identifiers = null,
            bool? checkSender = null)
        {
            var self = MacSecurity.CopySelfDesignatedRequirement() ?? throw new InvalidOperationException("The test process has no designated requirement.");
            MacPrivilegedDaemonDefinition Define(PrivilegedActionDeclaration[] declared) => new(
                "eu.runic.tests.privileged", "ABCDE12345", "eu.runic.tests", "eu.runic.tests.privileged",
                declared.Select(a => new MacPrivilegedRight(a.Id, $"Runic tests want to run {a.Id}.") { ProgramIdentifier = identifiers?.GetValueOrDefault(a.Id) }))
            {
                ApplicationRequirement = applicationRequirement ?? self,
                DaemonRequirement = self,
                ProgramRequirement = programRequirement ?? "anchor apple",
            };
            _queue = MacXpc.CreateQueue("eu.runic.tests.daemon", 0);
            _listener = MacXpc.CreateConnection(0, _queue);
            // The JIT build runs in the dotnet host, which the daemon refuses as a client (HardenedClientAsync), so the
            // other cases check each run's sender only in the hardened NativeAOT executable.
            var server = new MacPrivilegedDaemonServer(actions, Define(actions), Authority, null) { CheckSender = checkSender ?? SelfHardened() };
            server.Listen(_listener);
            _endpoint = MacXpc.CreateEndpoint(_listener);
            var client = Define(clientActions ?? actions);
            var host = new EndpointHost(_endpoint, daemonRequirement ?? self);
            Provider = new MacPrivilegedProvider(clientActions ?? actions, client, host);
        }

        public TestAuthority Authority { get; } = new();
        public MacPrivilegedProvider Provider { get; }

        public void Dispose()
        {
            MacXpc.Cancel(_listener);
            MacXpc.Release(_endpoint);
            MacXpc.Release(_listener);
        }
    }

    internal sealed class EndpointHost(nint endpoint, string daemonRequirement) : MacPrivilegedHost
    {
        internal static readonly byte[] Form = [.. Enumerable.Range(7, 32).Select(i => (byte)i)];
        internal override PlatformUnavailableReason? CheckAvailable(MacPrivilegedDaemonDefinition daemon) => null;
        internal override IMacPrivilegedChannel Connect(MacPrivilegedDaemonDefinition daemon) => MacXpcChannel.ConnectEndpoint(endpoint, daemonRequirement);
        internal override MacAuthorizationGrant Authorize(string right) => new(0, Form, null);
        internal override IReadOnlyDictionary<string, string> PassedEnvironment() => new Dictionary<string, string> { ["LANG"] = "en_US.UTF-8", ["LC_ALL"] = "bad value" };
    }

    /// <summary>Accepts the external form the test host sends; a gate holds the check to cancel before the start.</summary>
    internal sealed class TestAuthority : IMacDaemonAuthority
    {
        public volatile int CheckStatus;
        public int Checks, Ensured;
        public SemaphoreSlim? Gate;

        public int EnsureRight(MacPrivilegedRight right)
        {
            Interlocked.Increment(ref Ensured);
            return 0;
        }

        public int Check(string name, byte[] externalForm)
        {
            Interlocked.Increment(ref Checks);
            Gate?.Wait();
            if (!externalForm.AsSpan().SequenceEqual(EndpointHost.Form)) return MacPrivilegedOutcome.AuthorizationDenied;
            return CheckStatus;
        }
    }

    internal static string Dump(IEnumerable<PrivilegedActionOutput> lines) => string.Join(" | ", lines.Select(l => $"{l.Channel}:{l.Text}"));
}
