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
        Check(await first.WaitAsync(TimeSpan.FromSeconds(30)), "first line");
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

    [Test, Explicit, Category("NativePrivileged"), DisplayName("native: a program that users can change runs only with the required signature")]
    public async Task ProgramSignatureAsync()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var directory = Directory.CreateTempSubdirectory("runic-privileged-");
        try
        {
            // An Apple-signed copy outside the system volume: its path is the user's, so its signature decides.
            var echo = Path.Combine(directory.FullName, "echo");
            File.Copy("/bin/echo", echo);
            var script = Path.Combine(directory.FullName, "script");
            File.WriteAllText(script, "#!/bin/sh\necho unsigned\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var signed = new PrivilegedActionDeclaration("eu.runic.tests.copy", echo) { MaximumArguments = 1 };
            var unsigned = new PrivilegedActionDeclaration("eu.runic.tests.script", script);
            var missing = new PrivilegedActionDeclaration("eu.runic.tests.missing", Path.Combine(directory.FullName, "missing"));

            using (var daemon = new InProcessDaemon([signed, unsigned, missing], programRequirement: "anchor apple"))
            {
                var lines = new List<PrivilegedActionOutput>();
                var result = await daemon.Provider.RunAsync(new(signed, ["signed"]) { Output = lines.Add });
                Check(result is PlatformResult<PrivilegedActionCompletion>.Success { Value.ExitCode: 0 } && lines.Single().Text == "signed",
                    $"checked on disk and in the suspended process: {Describe(result)} {Dump(lines)}");
                result = await daemon.Provider.RunAsync(new(unsigned, []));
                Check(result is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.InvalidData, Diagnostic.Code: 3 }, "unsigned: " + Describe(result));
                result = await daemon.Provider.RunAsync(new(missing, []));
                Check(result is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.InvalidData, Diagnostic.Code: 1 }, "missing: " + Describe(result));
            }
            using (var daemon = new InProcessDaemon([signed], programRequirement: "identifier \"eu.runic.tests.nothing\" and anchor apple generic"))
            {
                var result = await daemon.Provider.RunAsync(new(signed, ["x"]));
                Check(result is PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.InvalidData, Diagnostic.Code: 3 }, "another signature: " + Describe(result));
            }
        }
        finally { directory.Delete(recursive: true); }
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
            Check(daemon.Authority.Checks == 0 && daemon.Authority.Ensured == 0, "nothing was sent to the daemon");
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
        var definition = MacSystemAuthority.CreateDefinition(right.ActionId);
        try
        {
            Check(!MacSystemAuthority.Matches(definition, right), "no prompt yet");
            var prompts = MacSecurity.CreateDictionary();
            MacSecurity.Set(prompts, "", MacSecurity.CreateString(right.Message));
            MacSecurity.Set(definition, "default-prompt", prompts);
            Check(MacSystemAuthority.Matches(definition, right), "the daemon's own definition matches");
            Check(!MacSystemAuthority.Matches(definition, new MacPrivilegedRight(right.ActionId, "Another message.")), "another message");
            MacSecurity.Set(definition, "timeout", MacSecurity.CreateNumber(0));
            Check(!MacSystemAuthority.Matches(definition, right), "another timeout");
        }
        finally { MacSecurity.Release(definition); }
    }

    /// <summary>The daemon's server on an anonymous listener in this process, and a provider that reaches it through its endpoint.</summary>
    internal sealed class InProcessDaemon : IDisposable
    {
        private readonly nint _queue, _listener, _endpoint;

        public InProcessDaemon(PrivilegedActionDeclaration[] actions, string? programRequirement = null, string? daemonRequirement = null,
            string? applicationRequirement = null, PrivilegedActionDeclaration[]? clientActions = null)
        {
            var self = MacSecurity.CopySelfDesignatedRequirement() ?? throw new InvalidOperationException("The test process has no designated requirement.");
            MacPrivilegedDaemonDefinition Define(PrivilegedActionDeclaration[] declared) => new(
                "eu.runic.tests.privileged", "ABCDE12345", "eu.runic.tests", "eu.runic.tests.privileged",
                declared.Select(a => new MacPrivilegedRight(a.Id, $"Runic tests want to run {a.Id}.")))
            {
                ApplicationRequirement = applicationRequirement ?? self,
                DaemonRequirement = self,
                ProgramRequirement = programRequirement ?? "anchor apple",
            };
            _queue = MacXpc.CreateQueue("eu.runic.tests.daemon", 0);
            _listener = MacXpc.CreateConnection(0, _queue);
            var server = new MacPrivilegedDaemonServer(actions, Define(actions), Authority, null);
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
