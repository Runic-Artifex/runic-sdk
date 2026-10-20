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
        Throws<NotSupportedException>(() => Pattern(@"(a)\1").AcceptsArguments([]), "backreferences need backtracking");
        Throws<NotSupportedException>(() => Pattern("(?=a)a").AcceptsArguments([]), "lookarounds need backtracking");
        Throws<ArgumentException>(() => Pattern("a)|(b").AcceptsArguments([]), "a pattern that would escape the anchors");
        Throws<ArgumentException>(() => Pattern("(?x)[a-z]+ # letters").AcceptsArguments([]), "a comment that swallows the anchor");
        Throws<NotSupportedException>(() => Pattern(@"(a)\1").CreateArgumentVector([]), "the argument vector checks the pattern too");
        var newline = new PrivilegedActionDeclaration("org.example.a", "/x") { MaximumArguments = 1, ArgumentPattern = "[^/]+" };
        Check(newline.AcceptsArguments(["a\nb"]), "a negated class matches a line break, as the documentation warns");
    }

    // #315: a NonBacktracking regex costs tens of milliseconds under the JIT, so a declaration compiles its pattern on
    // the first AcceptsArguments rather than when the generated declarations are read at startup. An invalid pattern,
    // which the build rejects for RunicPrivilegedAction items (RUNICPA008), therefore cannot fail before that.
    [Test, DisplayName("declaration: the pattern is compiled on the first argument check, not on creation")]
    public async Task PatternCompiledOnFirstCheckAsync()
    {
        var invalid = Pattern(@"(a)\1");
        Check(invalid.ArgumentPattern == @"(a)\1", "the pattern is kept as written");
        Throws<NotSupportedException>(() => invalid.AcceptsArguments(["a"]), "the first check compiles it");
        Throws<NotSupportedException>(() => invalid.AcceptsArguments(["a"]), "an invalid pattern fails every check");

        var action = new PrivilegedActionDeclaration("org.example.app.activate", "/usr/libexec/app/helper") { MaximumArguments = 1, ArgumentPattern = "switch|boot" };
        var checks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => action.AcceptsArguments(["boot"]) && !action.AcceptsArguments(["reboot"]))).ToArray();
        Check((await Task.WhenAll(checks)).All(result => result), "concurrent first checks agree");
        Check(action.AcceptsArguments(["switch"]) && !action.AcceptsArguments(["switchboot"]), "the compiled pattern is reused");
    }

    private static PrivilegedActionDeclaration Pattern(string pattern) =>
        new("org.example.a", "/x") { MaximumArguments = 1, ArgumentPattern = pattern };

    // #294: several operations of one program share one action, so auth_admin_keep keeps one authorization for them,
    // and each still declares its arguments by position.
    [Test, DisplayName("declaration: commands check the arguments after their name by position")]
    public void Commands()
    {
        var store = "/nix/store/" + new string('a', 32) + "-nixos-system-host";
        var action = new PrivilegedActionDeclaration("org.example.app.system", "/usr/libexec/app/helper")
        {
            Verb = "--privileged",
            MaximumArguments = 3,
            Commands =
            [
                new("activate-output", "switch|boot", "/nix/store/[a-z0-9]{32}-nixos-system-[A-Za-z0-9._+-]+"),
                new("rollback"),
                new("gc", "[1-9][0-9]{0,2}") { MinimumArguments = 0 },
            ],
        };
        Check(action.AcceptsArguments(["activate-output", "switch", store]), "activate-output");
        Check(!action.AcceptsArguments(["activate-output", store, "switch"]), "each pattern applies to its position only");
        Check(!action.AcceptsArguments(["activate-output", "switch"]), "a required argument is missing");
        Check(action.AcceptsArguments(["rollback"]), "a command without arguments");
        Check(!action.AcceptsArguments(["rollback", "1"]), "no more arguments than patterns");
        Check(action.AcceptsArguments(["gc"]) && action.AcceptsArguments(["gc", "100"]), "an optional trailing argument");
        Check(!action.AcceptsArguments(["gc", "0"]) && !action.AcceptsArguments(["gc", "1000"]), "the optional argument matches its pattern completely");
        Check(!action.AcceptsArguments([]), "an action with commands needs one");
        Check(!action.AcceptsArguments(["switch"]) && !action.AcceptsArguments(["GC"]), "unknown commands; names are compared ordinally");
        Check(!action.AcceptsArguments(["gc", "1\0"]), "NUL is still refused");
        Check(action.CreateArgumentVector(["gc", "5"]).SequenceEqual(["--privileged", "gc", "5"]), "the verb, then the command");
        Check(PolkitHelper.MatchCore([action], ["--privileged", "activate-output", "boot", store], out var rest) == action
            && rest.SequenceEqual(["activate-output", "boot", store]), "the helper matches by command too");
        Check(PolkitHelper.MatchCore([action], ["--privileged", "activate-output", "boot", "/etc"], out _) is null, "and refuses a command's wrong argument");

        var bounded = new PrivilegedActionDeclaration("org.example.app.bounded", "/x") { MaximumArguments = 1, Commands = [new("gc", "[0-9]+")] };
        Check(bounded.AcceptsArguments(["gc"]) is false && !bounded.AcceptsArguments(["gc", "1"]), "MaximumArguments still counts the name and its arguments");
        var shared = new PrivilegedActionDeclaration("org.example.app.shared", "/x") { MaximumArguments = 2, ArgumentPattern = "[a-z0-9]+", Commands = [new("gc", "[0-9]+")] };
        Check(shared.AcceptsArguments(["gc", "1"]) && !new PrivilegedActionDeclaration("org.example.app.upper", "/x") { MaximumArguments = 2, ArgumentPattern = "[a-z]+", Commands = [new("gc", "[0-9]+")] }.AcceptsArguments(["gc", "1"]),
            "ArgumentPattern still applies to every argument");

        Check(new PrivilegedActionDeclaration("org.example.a", "/x").Commands.IsEmpty, "no commands by default");
        Check(new PrivilegedActionDeclaration("org.example.a", "/x") { Commands = default }.Commands.IsEmpty, "default is empty");
        Throws<ArgumentException>(() => _ = new PrivilegedActionDeclaration("org.example.a", "/x") { Commands = [new("gc"), new("gc", "[0-9]+")] }, "two commands with one name");
        Throws<ArgumentException>(() => _ = new PrivilegedActionDeclaration("org.example.a", "/x") { Commands = [null!] }, "a null command");
        Throws<ArgumentException>(() => _ = new PrivilegedActionCommand(""), "empty name");
        Throws<ArgumentException>(() => _ = new PrivilegedActionCommand("a\0b"), "NUL in the name");
        Throws<ArgumentException>(() => _ = new PrivilegedActionCommand("gc", [.. Enumerable.Repeat("x", PrivilegedActionDeclaration.ArgumentLimit)]), "too many patterns");
        Throws<ArgumentException>(() => _ = new PrivilegedActionCommand("gc", null!, "x"), "a null pattern");
        Throws<ArgumentOutOfRangeException>(() => _ = new PrivilegedActionCommand("gc", "x") { MinimumArguments = 2 }, "more required arguments than patterns");
        Throws<ArgumentOutOfRangeException>(() => _ = new PrivilegedActionCommand("gc", "x") { MinimumArguments = -1 }, "negative");
        var lookaround = new PrivilegedActionDeclaration("org.example.a", "/x") { MaximumArguments = 2, Commands = [new("rollback"), new("gc", "(?=a)a")] };
        Throws<NotSupportedException>(() => lookaround.AcceptsArguments(["rollback"]), "every command's patterns are compiled on the first check");
        Throws<ArgumentException>(() => new PrivilegedActionDeclaration("org.example.a", "/x") { MaximumArguments = 2, Commands = [new("gc", "a)|(b")] }.AcceptsArguments(["gc", "a"]),
            "a command pattern cannot escape its anchors");

        Check(new PrivilegedActionCommand("gc", "[0-9]+") { MinimumArguments = 0 }.Equals(new PrivilegedActionCommand("gc", "[0-9]+") { MinimumArguments = 0 })
            && new PrivilegedActionCommand("gc", "[0-9]+").GetHashCode() == new PrivilegedActionCommand("gc", "[0-9]+").GetHashCode(), "commands compare by value");
        Check(!new PrivilegedActionCommand("gc", "[0-9]+").Equals(new PrivilegedActionCommand("gc", "[0-9]+") { MinimumArguments = 0 })
            && !new PrivilegedActionCommand("gc", "[0-9]+").Equals(new PrivilegedActionCommand("gc", "[0-8]+")), "and differ by any part");
    }

    [Test, DisplayName("provider: a request's declaration must have the provider's commands")]
    public async Task ProviderComparesCommandsAsync()
    {
        PrivilegedActionDeclaration Declare(string pattern) =>
            new("org.example.app.system", "/opt/app/helper") { Verb = "run", MaximumArguments = 2, Commands = [new("gc", pattern)] };
        // Without pkexec, a request the provider accepts is unavailable rather than run.
        var actions = new PolkitActionRunner([Declare("[0-9]+")], new PolkitOptions(), new PkexecTests.FakeHost(null));
        Check(await actions.RunAsync(new PrivilegedActionRequest(Declare("[0-9]+"), ["gc", "1"])) is PlatformResult<PrivilegedActionCompletion>.Unavailable,
            "an equal declaration is the provider's");
        await ThrowsAsync<ArgumentException>(() => actions.RunAsync(new PrivilegedActionRequest(Declare("[0-9a-z]+"), ["gc", "1"])).AsTask(),
            "a declaration with other commands is not the provider's");
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
        var collect = new PrivilegedActionDeclaration("org.example.app.collect", "/opt/app/wrapper") { Verb = "collect" };
        var bare = new PrivilegedActionDeclaration("org.example.tool", "/opt/tool/helper") { MaximumArguments = 1 };
        PrivilegedActionDeclaration[] all = [activate, collect];
        Check(PolkitHelper.MatchCore(all, ["activate", "boot"], out var rest) == activate && rest.SequenceEqual(["boot"]), "activate");
        Check(PolkitHelper.MatchCore(all, ["collect"], out rest) == collect && rest.IsEmpty, "the verb alone decides, whichever path pkexec ran");
        Check(PolkitHelper.MatchCore(all, ["activate", "reboot"], out _) is null, "argument outside the pattern");
        Check(PolkitHelper.MatchCore(all, ["collect", "extra"], out _) is null, "too many arguments");
        Check(PolkitHelper.MatchCore(all, ["unknown"], out _) is null, "unknown verb");
        Check(PolkitHelper.MatchCore(all, [], out _) is null, "no verb");
        Check(PolkitHelper.MatchCore([bare], ["x"], out rest) == bare && rest.SequenceEqual(["x"]), "an action without a verb on its own");
        Check(PolkitHelper.MatchCore([activate, bare], ["activate", "boot"], out _) is null, "an action without a verb beside others fails closed");
        var twin = new PrivilegedActionDeclaration("org.example.other", "/opt/other/helper") { Verb = "activate", MaximumArguments = 1 };
        Check(PolkitHelper.MatchCore([activate, twin], ["activate", "boot"], out _) is null, "a verb declared twice fails closed");
        Check(PolkitHelper.GetCallerUserId() is null || Environment.GetEnvironmentVariable("PKEXEC_UID") is not null, "not started by pkexec");
        if (!OperatingSystem.IsWindows() && Environment.UserName != "root")
        {
            Check(!PolkitHelper.Initialize(), "not root: the helper refuses to act");
            Check(PolkitHelper.Match(all, ["collect"], out _) is null, "and Match fails closed");
        }
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
        Throws<ArgumentException>(() => PolkitPrivilegedActions.Create([a, new PrivilegedActionDeclaration("org.example.app.d", "/opt/app/wrapper") { Verb = "a" }]),
            "the same verb for another program: the helper cannot tell which path pkexec matched");
        Throws<ArgumentException>(() => PolkitPrivilegedActions.Create([a, new PrivilegedActionDeclaration("org.example.app.e", "/opt/other") ]), "a verbless action beside another program's");
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
