using System.Collections.Immutable;
using Runic.Platform;
using Runic.Platform.Linux.Polkit;
using TUnit.Core;

internal sealed class DeclarationTests
{
    [Test, DisplayName("declaration: the stable action ID format")]
    public void IdFormat()
    {
        foreach (var id in new[] { "org.example.app", "org.nixos.nix-manager.activate", "dev.runic.privileged-smoke.allowed", "a.b", "org.example.2fa" })
            Check(PrivilegedActionDeclaration.IsValidId(id), $"valid: {id}");
        foreach (var id in new[] { "", "org", "Org.example.app", "org.Example", "org..app", ".org.app", "org.app.", "org.-app", "1org.app", "org.app_x", "org.app x", "org.äpp", new string('a', 254) + ".b" })
            Check(!PrivilegedActionDeclaration.IsValidId(id), $"invalid: {id}");
        Throws<ArgumentException>(() => _ = new PrivilegedActionDeclaration("Not.Valid", "/usr/bin/true"), "invalid ID");
    }

    [Test, DisplayName("declaration: the program is an absolute, normalized path")]
    public void ProgramPath()
    {
        _ = new PrivilegedActionDeclaration("org.example.app", "/usr/libexec/app/helper");
        foreach (var program in new[] { "", "/", "helper", "./helper", "/usr/../bin/sh", "/usr/./bin/sh", "/usr//bin/sh", "/usr/bin/", "/usr/bin/sh\0x" })
            Throws<ArgumentException>(() => _ = new PrivilegedActionDeclaration("org.example.app", program), $"program {program}");
    }

    [Test, DisplayName("declaration: arguments are bounded by count, length, NUL and the full-match pattern")]
    public void Arguments()
    {
        var action = new PrivilegedActionDeclaration("org.example.app.activate", "/usr/libexec/app/helper")
        {
            Verb = "activate",
            MaximumArguments = 2,
            ArgumentPattern = "switch|boot|/nix/store/[a-z0-9]{32}-[^/]+",
        };
        var store = "/nix/store/" + new string('a', 32) + "-nixos-system-host";
        Check(action.AcceptsArguments([]), "none");
        Check(action.AcceptsArguments(["switch", store]), "two matching");
        Check(!action.AcceptsArguments(["switch", store, "boot"]), "too many");
        Check(!action.AcceptsArguments(["switchboot"]), "the pattern matches the whole argument, not a part");
        Check(!action.AcceptsArguments(["boot\n"]), "a trailing newline is not a match");
        Check(!action.AcceptsArguments([store + "/../../etc"]), "path escapes do not match");
        Check(!action.AcceptsArguments(["sw\0itch"]), "NUL");
        Check(action.CreateArgumentVector(["boot"]).SequenceEqual(["activate", "boot"]), "the verb comes first");
        Throws<ArgumentException>(() => action.CreateArgumentVector(["reboot"]), "rejected arguments throw");

        var open = new PrivilegedActionDeclaration("org.example.app.any", "/usr/libexec/app/helper") { MaximumArguments = 1 };
        Check(open.AcceptsArguments(["; rm -rf / $(id)"]), "without a pattern any text is one argument");
        Check(!open.AcceptsArguments([new string('x', PrivilegedActionDeclaration.ArgumentLengthLimit + 1)]), "length limit");
        Check(open.CreateArgumentVector(["x"]).SequenceEqual(["x"]), "no verb");

        Throws<ArgumentOutOfRangeException>(() => _ = new PrivilegedActionDeclaration("org.example.a", "/x") { MaximumArguments = 65 }, "count limit");
        Throws<ArgumentException>(() => _ = new PrivilegedActionDeclaration("org.example.a", "/x") { Verb = "" }, "empty verb");
        Throws<NotSupportedException>(() => _ = new PrivilegedActionDeclaration("org.example.a", "/x") { ArgumentPattern = @"(a)\1" }, "backreferences need backtracking");
    }

    [Test, DisplayName("declaration: the unavailable provider checks arguments and reports its reason")]
    public async Task UnavailableAsync()
    {
        var action = new PrivilegedActionDeclaration("org.example.app", "/usr/libexec/app/helper") { MaximumArguments = 1 };
        var actions = DesktopPrivilegedActions.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        Check(await actions.RunAsync(new PrivilegedActionRequest(action, ["x"])) is PlatformResult<PrivilegedActionCompletion>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured }, "reason");
        await ThrowsAsync<ArgumentException>(() => actions.RunAsync(new PrivilegedActionRequest(action, ["x", "y"])).AsTask(), "arguments");
        await ThrowsAsync<OperationCanceledException>(() => actions.RunAsync(new PrivilegedActionRequest(action, []), new CancellationToken(true)).AsTask(), "cancellation");
        Throws<ArgumentOutOfRangeException>(() => DesktopPrivilegedActions.Unavailable((PlatformUnavailableReason)99), "undefined reason");
    }

    [Test, DisplayName("helper: matches this executable, the verb and the declared arguments")]
    public void HelperMatch()
    {
        var activate = new PrivilegedActionDeclaration("org.example.app.activate", "/opt/app/helper") { Verb = "activate", MaximumArguments = 1, ArgumentPattern = "switch|boot" };
        var collect = new PrivilegedActionDeclaration("org.example.app.collect", "/opt/app/helper") { Verb = "collect" };
        var other = new PrivilegedActionDeclaration("org.example.other", "/opt/other/helper") { Verb = "activate", MaximumArguments = 1 };
        PrivilegedActionDeclaration[] all = [other, activate, collect];
        Check(PolkitHelper.MatchCore(all, ["activate", "boot"], "/opt/app/helper", out var rest) == activate && rest.SequenceEqual(["boot"]), "activate");
        Check(PolkitHelper.MatchCore(all, ["collect"], "/opt/app/helper", out rest) == collect && rest.IsEmpty, "collect");
        Check(PolkitHelper.MatchCore(all, ["activate", "reboot"], "/opt/app/helper", out _) is null, "argument outside the pattern");
        Check(PolkitHelper.MatchCore(all, ["collect", "extra"], "/opt/app/helper", out _) is null, "too many arguments");
        Check(PolkitHelper.MatchCore(all, ["unknown"], "/opt/app/helper", out _) is null, "unknown verb");
        Check(PolkitHelper.MatchCore(all, [], "/opt/app/helper", out _) is null, "no verb");
        Check(PolkitHelper.MatchCore(all, ["activate", "boot"], "/tmp/copy/helper", out _) is null, "two programs declare the verb and neither is this one");
        Check(PolkitHelper.MatchCore(all, ["activate", "x"], "/opt/other/helper", out rest) == other && rest.SequenceEqual(["x"]), "this executable settles the tie");
        Check(PolkitHelper.MatchCore([activate, collect], ["collect"], "/nix/store/abc-app/lib/app/helper", out _) == collect,
            "a wrapper script may start the program, so the process path need not be the declared program");
        Check(PolkitHelper.GetCallerUserId() is null || Environment.GetEnvironmentVariable("PKEXEC_UID") is not null, "not started by pkexec");
    }

    [Test, DisplayName("provider: actions must be distinguishable by pkexec")]
    public void ProviderDeclarations()
    {
        var a = new PrivilegedActionDeclaration("org.example.app.a", "/opt/app/helper") { Verb = "a" };
        var b = new PrivilegedActionDeclaration("org.example.app.b", "/opt/app/helper") { Verb = "b" };
        var bare = new PrivilegedActionDeclaration("org.example.app.bare", "/opt/app/helper");
        _ = PolkitPrivilegedActions.Create([a, b]);
        Throws<ArgumentException>(() => PolkitPrivilegedActions.Create([a, a]), "duplicate ID");
        Throws<ArgumentException>(() => PolkitPrivilegedActions.Create([a, new PrivilegedActionDeclaration("org.example.app.c", "/opt/app/helper") { Verb = "a" }]), "same program and verb");
        Throws<ArgumentException>(() => PolkitPrivilegedActions.Create([a, bare]), "an action without a verb shadows the others");
        Throws<ArgumentException>(() => PolkitPrivilegedActions.Create([a], new PolkitOptions { PkexecPath = "pkexec" }), "relative pkexec");
    }

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
}
