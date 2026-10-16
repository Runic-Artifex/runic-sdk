using System.Diagnostics;
using System.Xml;
using TUnit.Core;
using static DeclarationTests;

/// <summary>
/// Builds small projects against the shipped targets, Runic.Platform's (validation and declarations) and this
/// package's (the policy), and reads the policy and C# they generate.
/// </summary>
internal sealed class PolicyGenerationTests
{
    private static readonly string PlatformTargets = Path.Combine(AppContext.BaseDirectory, "platform-targets", "Runic.Platform.targets");
    private static readonly string Targets = Path.Combine(AppContext.BaseDirectory, "polkit-targets", "Runic.Platform.Linux.Polkit.targets");

    [Test, DisplayName("policy: valid XML with escaped text, defaults, annotations and matching declarations")]
    public async Task GeneratesPolicyAndDeclarationsAsync()
    {
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="org.example.app.activate" Program="/nix/store/abc-app/libexec/helper" Verb="activate"
                                   Description="Activate &quot;the&quot; system" Message="Authentication is required to change &lt;the system&gt; &amp; reboot."
                                   IconName="system-software-update" AllowActive="auth_admin_keep" MaximumArguments="2"
                                   ArgumentPattern="switch|boot|/nix/store/[a-z0-9]{32}-nixos-system-[^/\\]+" />
            <RunicPrivilegedAction Include="org.example.app.collect-garbage" Program="/nix/store/abc-app/libexec/helper" Verb="gc"
                                   Description="Collect garbage" Message="Authentication is required to collect garbage." AllowGui="true" />
            <RunicPrivilegedAction Include="org.example.tool" Program="/usr/libexec/tool" Verb="run" Name="Tool"
                                   Description="Run the tool" Message="Authentication is required to run the tool." AllowAny="no" />
            """);
        var (exit, output) = await project.BuildAsync();
        Check(exit == 0, output);

        var policy = new XmlDocument();
        using (var reader = XmlReader.Create(project.Policy, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            policy.Load(reader);
        Check(File.ReadAllText(project.Policy).StartsWith("""
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE policyconfig PUBLIC "-//freedesktop//DTD PolicyKit Policy Configuration 1.0//EN" "http://www.freedesktop.org/standards/PolicyKit/1/policyconfig.dtd">
            """, StringComparison.Ordinal), "declaration and doctype");
        var actions = policy.SelectNodes("/policyconfig/action")!.Cast<XmlElement>().ToList();
        Check(actions.Select(a => a.GetAttribute("id")).SequenceEqual(["org.example.app.activate", "org.example.app.collect-garbage", "org.example.tool"]), "IDs in order");

        var activate = actions[0];
        Check(Text(activate, "description") == "Activate \"the\" system", "description");
        var message = activate.SelectSingleNode("message")!;
        // polkitd keeps only the last piece of text expat reports, so escaped text must be one CDATA section.
        Check(message.ChildNodes.Count == 1 && message.FirstChild is XmlCDataSection && message.InnerText == "Authentication is required to change <the system> & reboot.",
            $"message: {message.OuterXml}");
        Check(Text(activate, "icon_name") == "system-software-update", "icon");
        Check(Text(activate, "defaults/allow_any") == "auth_admin" && Text(activate, "defaults/allow_inactive") == "auth_admin"
            && Text(activate, "defaults/allow_active") == "auth_admin_keep", "defaults");
        Check(Annotation(activate, "org.freedesktop.policykit.exec.path") == "/nix/store/abc-app/libexec/helper", "exec.path");
        Check(Annotation(activate, "org.freedesktop.policykit.exec.argv1") == "activate", "argv1");
        Check(Annotation(activate, "org.freedesktop.policykit.exec.allow_gui") is null, "no allow_gui unless asked: pkexec reads any value as true");
        Check(Annotation(actions[1], "org.freedesktop.policykit.exec.allow_gui") == "true", "allow_gui");
        Check(Annotation(actions[2], "org.freedesktop.policykit.exec.argv1") == "run" && Text(actions[2], "defaults/allow_any") == "no", "another program, explicit default");
        Check(!output.Contains("RUNICPA014", StringComparison.Ordinal), "no warnings for auth_admin, auth_admin_keep and no");
        Check(actions[2].SelectSingleNode("icon_name") is null, "no icon");

        var source = File.ReadAllText(project.Source);
        Check(source.Contains("namespace Example.App;", StringComparison.Ordinal), "namespace");
        Check(source.Contains("internal static partial class PrivilegedActions", StringComparison.Ordinal), "class");
        Check(source.Contains("public static global::Runic.Platform.PrivilegedActionDeclaration Activate { get; } = new(\"org.example.app.activate\", \"/nix/store/abc-app/libexec/helper\")", StringComparison.Ordinal), source);
        Check(source.Contains("""ArgumentPattern = "switch|boot|/nix/store/[a-z0-9]{32}-nixos-system-[^/\\\\]+",""", StringComparison.Ordinal), "C# literal escaping");
        Check(source.Contains(" CollectGarbage { get; }", StringComparison.Ordinal) && source.Contains(" Tool { get; }", StringComparison.Ordinal), "property names");
        Check(source.Contains("Verb = \"gc\",", StringComparison.Ordinal) && source.Contains("MaximumArguments = 2,", StringComparison.Ordinal), "declaration values");
        Check(source.Contains("{ Activate, CollectGarbage, Tool }", StringComparison.Ordinal), "All");
        Check(source.Contains("/// <summary><c>org.example.app.activate</c>: Activate \"the\" system</summary>", StringComparison.Ordinal), "doc comment");

        var written = File.GetLastWriteTimeUtc(project.Policy);
        await Task.Delay(50);
        (exit, output) = await project.BuildAsync();
        Check(exit == 0 && File.GetLastWriteTimeUtc(project.Policy) == written, "an unchanged policy is not rewritten");
    }

    // #294: commands share their action's policy entry, so auth_admin_keep keeps one authorization for all of them.
    [Test, DisplayName("declarations: commands are generated with their action, which keeps one policy entry")]
    public async Task GeneratesCommandsAsync()
    {
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="org.example.app.system" Program="/opt/app/helper" Verb="--privileged" Name="System"
                                   Description="Change the system" Message="Authentication is required to change the system." AllowActive="auth_admin_keep" />
            <RunicPrivilegedAction Include="org.example.app.plain" Program="/opt/app/helper" Verb="plain" MaximumArguments="1"
                                   Description="Plain" Message="m" />
            <RunicPrivilegedCommand Include="activate-output" Action="org.example.app.system" Arguments="switch|boot;/nix/store/[a-z0-9]{32}-nixos-system-[A-Za-z0-9._+\x3B-]+" />
            <RunicPrivilegedCommand Include="rollback" Action="org.example.app.system" />
            <RunicPrivilegedCommand Include="gc" Action="org.example.app.system" Arguments="[1-9][0-9]{0,2}" MinimumArguments="0" />
            """);
        var (exit, output) = await project.BuildAsync();
        Check(exit == 0, output);
        var source = File.ReadAllText(project.Source);
        Check(source.Contains("""
                    Verb = "--privileged",
                    MaximumArguments = 3,
                    Commands = global::System.Collections.Immutable.ImmutableArray.Create(
                        new global::Runic.Platform.PrivilegedActionCommand("activate-output", "switch|boot", "/nix/store/[a-z0-9]{32}-nixos-system-[A-Za-z0-9._+\\x3B-]+"),
                        new global::Runic.Platform.PrivilegedActionCommand("rollback"),
                        new global::Runic.Platform.PrivilegedActionCommand("gc", "[1-9][0-9]{0,2}") { MinimumArguments = 0 }),
                };
            """.Replace("\r\n", "\n"), StringComparison.Ordinal), source);
        var plain = source[source.IndexOf(" Plain { get; }", StringComparison.Ordinal)..];
        Check(plain.Contains("MaximumArguments = 1,", StringComparison.Ordinal) && !plain[..plain.IndexOf("};", StringComparison.Ordinal)].Contains("Commands", StringComparison.Ordinal),
            "an action without commands is unchanged");

        var policy = new XmlDocument();
        using (var reader = XmlReader.Create(project.Policy, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            policy.Load(reader);
        var ids = policy.SelectNodes("/policyconfig/action")!.Cast<XmlElement>().Select(a => a.GetAttribute("id")).ToList();
        Check(ids.SequenceEqual(["org.example.app.system", "org.example.app.plain"]), $"one policy entry per action, not per command: {string.Join(", ", ids)}");
    }

    [Test, DisplayName("declarations: invalid commands are build errors and write nothing")]
    public async Task RejectsInvalidCommandsAsync()
    {
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="org.example.app.system" Program="/opt/app/helper" Verb="run" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.app.small" Program="/opt/app/helper" Verb="small" MaximumArguments="1" Description="d" Message="m" />
            <RunicPrivilegedCommand Include="orphan" Action="org.example.app.missing" />
            <RunicPrivilegedCommand Include="gc" Action="org.example.app.system" />
            <RunicPrivilegedCommand Include="gc" Action="org.example.app.system" Arguments="[0-9]+" />
            <RunicPrivilegedCommand Include="look" Action="org.example.app.system" Arguments="(?=a)a" />
            <RunicPrivilegedCommand Include="empty" Action="org.example.app.system" Arguments="a;;b" />
            <RunicPrivilegedCommand Include="minimum" Action="org.example.app.system" Arguments="a" MinimumArguments="2" />
            <RunicPrivilegedCommand Include="wide" Action="org.example.app.small" Arguments="a;b" />
            """);
        var (exit, output) = await project.BuildAsync();
        Check(exit != 0, output);
        foreach (var expected in new[]
        {
            "names the action 'org.example.app.missing', which no RunicPrivilegedAction item declares",
            "Privileged command 'gc' of 'org.example.app.system' is declared twice",
            "Privileged command 'look' of 'org.example.app.system' has a pattern for argument 1 that needs backtracking",
            "Privileged command 'empty' of 'org.example.app.system' has an empty pattern for argument 2",
            "Privileged command 'minimum' of 'org.example.app.system' has MinimumArguments '2'",
            "Privileged action 'org.example.app.small' has MaximumArguments 1, but its command 'wide' takes 3 arguments",
        })
            Check(output.Contains(expected, StringComparison.Ordinal), $"{expected} in {output}");
        Check(output.Contains("RUNICPA015", StringComparison.Ordinal) && output.Contains("RUNICPA008", StringComparison.Ordinal), output);
        Check(!File.Exists(project.Policy) && !File.Exists(project.Source), "nothing written");
        Check(!output.Contains("MSB4018", StringComparison.Ordinal), "the task did not crash");
    }

    [Test, DisplayName("policy: a sole action without a verb, and warnings for defaults that skip the administrator")]
    public async Task VerblessActionAndWeakDefaultsAsync()
    {
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="org.example.tool" Program="/usr/libexec/tool" Description="Run the tool"
                                   Message="Authentication is required to run the tool." AllowActive="yes" AllowInactive="auth_self_keep" />
            """);
        var (exit, output) = await project.BuildAsync();
        Check(exit == 0, output);
        Check(output.Split('\n').Count(l => l.Contains("warning RUNICPA014", StringComparison.Ordinal)) == 2, $"two warnings: {output}");
        Check(output.Contains("site rule", StringComparison.Ordinal), "yes points to site rules");
        var policy = new XmlDocument();
        using (var reader = XmlReader.Create(project.Policy, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            policy.Load(reader);
        var action = (XmlElement)policy.SelectSingleNode("/policyconfig/action")!;
        Check(Annotation(action, "org.freedesktop.policykit.exec.argv1") is null, "no argv1 for an action without a verb");
    }

    [Test, DisplayName("policy: RunicApplicationInstallPrefix is /usr unless the build sets it")]
    public async Task InstallPrefixDefaultsToUsrAsync()
    {
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="org.example.app.activate" Program="$(RunicApplicationInstallPrefix)/bin/example" Verb="activate"
                                   Description="Activate" Message="Authentication is required to activate." />
            """);
        foreach (var (arguments, program) in new[]
        {
            (Array.Empty<string>(), "/usr/bin/example"),
            (new[] { "-p:RunicApplicationInstallPrefix=/nix/store/abc-app" }, "/nix/store/abc-app/bin/example"),
        })
        {
            var (exit, output) = await project.BuildAsync(arguments);
            Check(exit == 0, output);
            var policy = new XmlDocument();
            using (var reader = XmlReader.Create(project.Policy, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
                policy.Load(reader);
            var action = (XmlElement)policy.SelectSingleNode("/policyconfig/action")!;
            Check(Annotation(action, "org.freedesktop.policykit.exec.path") == program, $"exec.path with {string.Join(' ', arguments)}");
            Check(File.ReadAllText(project.Source).Contains($"new(\"org.example.app.activate\", \"{program}\")", StringComparison.Ordinal), "declaration");
        }
    }

    [Test, DisplayName("policy: invalid declarations are build errors and write nothing")]
    public async Task RejectsInvalidDeclarationsAsync()
    {
        // Runic.Platform validates these once, before the policy target reads the items.
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="Org.Example.Bad" Program="/opt/a" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.relative" Program="helper" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.no-message" Program="/opt/b" Description="d" />
            <RunicPrivilegedAction Include="org.example.verb" Program="/opt/e" Verb="x" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.no-verb" Program="/opt/e" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.twice" Program="/opt/f" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.twice" Program="/opt/g" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.count" Program="/opt/h" Description="d" Message="m" MaximumArguments="99" />
            <RunicPrivilegedAction Include="org.example.pattern" Program="/opt/i" Description="d" Message="m" ArgumentPattern="(" />
            <RunicPrivilegedAction Include="org.example.all" Program="/opt/j" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.lookaround" Program="/opt/l" Verb="look" Description="d" Message="m" ArgumentPattern="(?=a)a" />
            <RunicPrivilegedAction Include="org.example.comment" Program="/opt/m" Verb="comment" Description="d" Message="m" ArgumentPattern="(?x)[a-z]+ # letters" />
            <RunicPrivilegedAction Include="org.example.mac" Program="/opt/n" Verb="mac" Name="MacOSDaemon" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.windows" Program="/opt/o" Verb="windows" Name="WindowsElevation" Description="d" Message="m" />
            """);
        var (exit, output) = await project.BuildAsync();
        Check(exit != 0, output);
        foreach (var code in new[] { "RUNICPA002", "RUNICPA003", "RUNICPA004", "RUNICPA005", "RUNICPA006", "RUNICPA008", "RUNICPA009", "RUNICPA011" })
            Check(output.Contains(code, StringComparison.Ordinal), $"{code} in {output}");
        Check(!File.Exists(project.Policy) && !File.Exists(project.Source), "nothing written");
        Check(output.Contains("'org.example.lookaround' has an ArgumentPattern that needs backtracking", StringComparison.Ordinal), "lookarounds fail at build time");
        Check(output.Contains("'org.example.comment' has an invalid ArgumentPattern", StringComparison.Ordinal), "a pattern valid only unanchored fails at build time");
        foreach (var name in new[] { "All", "MacOSDaemon", "WindowsElevation" })
            Check(output.Contains($"generates the property '{name}', which is not usable", StringComparison.Ordinal), $"the reserved name {name} is RUNICPA011");
        Check(!output.Contains("RUNICPA012", StringComparison.Ordinal) && !output.Contains("RUNICPA013", StringComparison.Ordinal), "the policy target did not run");
        Check(Count(output, "error RUNICPA004") == 1, $"each invalid item is reported once: {output}");
        Check(!output.Contains("MSB4018", StringComparison.Ordinal), "the task did not crash");
    }

    [Test, DisplayName("policy: values the policy cannot carry are build errors and write nothing")]
    public async Task RejectsInvalidPolicyValuesAsync()
    {
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="org.example.authorization" Program="/opt/c" Verb="authorization" Description="d" Message="m" AllowActive="sometimes" />
            <RunicPrivilegedAction Include="org.example.newline" Program="/opt/d" Verb="newline" Description="d" Message="first&#10;second" />
            <RunicPrivilegedAction Include="org.example.xml" Program="/opt/k" Verb="xml" Description="d" Message="$(BadText)" />
            <RunicPrivilegedAction Include="org.example.gui" Program="/opt/l" Verb="gui" Description="d" Message="m" AllowGui="maybe" />
            """);
        var (exit, output) = await project.BuildAsync("-p:BadText=not\uFFFEXML");
        Check(exit != 0, output);
        foreach (var code in new[] { "RUNICPA010", "RUNICPA012", "RUNICPA013" })
            Check(output.Contains(code, StringComparison.Ordinal), $"{code} in {output}");
        Check(output.Contains("'org.example.newline' has a line break", StringComparison.Ordinal), "a line break is RUNICPA013");
        Check(output.Contains("'org.example.xml' has a line break", StringComparison.Ordinal), "a character XML cannot carry is RUNICPA013, not a task exception");
        Check(!File.Exists(project.Policy) && !File.Exists(project.Source), "neither the policy nor the declarations are written");
        Check(!output.Contains("MSB4018", StringComparison.Ordinal), "the task did not crash");
    }

    [Test, DisplayName("declarations: per-platform metadata passes through, and validation runs on its own")]
    public async Task PlatformMetadataAndValidationTargetAsync()
    {
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="org.example.app.activate" Program="/opt/example/helper" Verb="activate"
                                   WindowsProgram="helpers\example-helper.exe" MacProgram="Contents/MacOS/helper"
                                   Description="Activate" Message="Authentication is required to activate." />
            """, extraProperties: "<RunicPrivilegedActionsClassName>Actions</RunicPrivilegedActionsClassName><RunicPrivilegedActionsNamespace>Other.Space</RunicPrivilegedActionsNamespace>");
        var (exit, output) = await project.BuildAsync("-t:RunicValidatePrivilegedActions");
        Check(exit == 0, output);
        Check(!File.Exists(project.Source) && !File.Exists(project.Policy), "validation writes nothing");
        (exit, output) = await project.BuildAsync();
        Check(exit == 0, output);
        var source = File.ReadAllText(project.Source);
        Check(source.Contains("namespace Other.Space;", StringComparison.Ordinal) && source.Contains("internal static partial class Actions", StringComparison.Ordinal), source);
        Check(!source.Contains("helpers", StringComparison.Ordinal) && !source.Contains("Contents/MacOS", StringComparison.Ordinal), "platform metadata is not part of the shared declaration");
        var properties = await project.EvaluateAsync("RunicPrivilegedActionsClassName", "RunicPrivilegedActionsNamespace", "RunicPrivilegedActionsReservedNames");
        Check(properties.Contains("\"RunicPrivilegedActionsClassName\": \"Actions\"", StringComparison.Ordinal)
            && properties.Contains("\"RunicPrivilegedActionsNamespace\": \"Other.Space\"", StringComparison.Ordinal)
            && properties.Contains("All;MacOSDaemon;WindowsElevation", StringComparison.Ordinal), $"evaluated defaults: {properties}");
    }

    [Test, DisplayName("declarations: the namespace defaults to the project's RootNamespace at evaluation time")]
    public async Task EvaluatedDefaultsAsync()
    {
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="org.example.app.activate" Program="/opt/example/helper" Description="Activate" Message="m" />
            """);
        var properties = await project.EvaluateAsync("RunicPrivilegedActionsClassName", "RunicPrivilegedActionsNamespace");
        Check(properties.Contains("\"RunicPrivilegedActionsClassName\": \"PrivilegedActions\"", StringComparison.Ordinal)
            && properties.Contains("\"RunicPrivilegedActionsNamespace\": \"Example.App\"", StringComparison.Ordinal), properties);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string? Text(XmlNode node, string path) => node.SelectSingleNode(path)?.InnerText;

    private static string? Annotation(XmlElement action, string key) =>
        action.SelectNodes("annotate")!.Cast<XmlElement>().FirstOrDefault(a => a.GetAttribute("key") == key)?.InnerText;

    private sealed class FixtureProject : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "runic-polkit-policy-" + Guid.NewGuid().ToString("N")[..8]);

        public FixtureProject(string items, string extraProperties = "")
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "fixture.proj"), $"""
                <Project>
                  <PropertyGroup>
                    <AssemblyName>Fixture</AssemblyName>
                    <RootNamespace>Example.App</RootNamespace>
                    <IntermediateOutputPath>obj/</IntermediateOutputPath>
                    <RunicPolkitPolicyName>org.example.app</RunicPolkitPolicyName>
                    {extraProperties}
                  </PropertyGroup>
                  <ItemGroup>
                {items}
                  </ItemGroup>
                  <Import Project="{PlatformTargets}" />
                  <Import Project="{Targets}" />
                </Project>
                """);
        }

        public string Policy => Path.Combine(_directory, "obj", "runic-privileged", "org.example.app.policy");
        public string Source => Path.Combine(_directory, "obj", "runic-privileged", "PrivilegedActions.g.cs");

        public async Task<string> EvaluateAsync(params string[] properties)
        {
            var (exit, output) = await RunAsync(["msbuild", "fixture.proj", "-nologo", .. properties.Select(p => $"-getProperty:{p}")]);
            Check(exit == 0, output);
            return output;
        }

        public Task<(int ExitCode, string Output)> BuildAsync(params string[] extra) =>
            RunAsync(["msbuild", "fixture.proj", .. extra.Any(e => e.StartsWith("-t:", StringComparison.Ordinal)) ? Array.Empty<string>() : ["-t:RunicGeneratePrivilegedActions"], "-nologo", "-nodeReuse:false", "-v:m", .. extra]);

        private async Task<(int ExitCode, string Output)> RunAsync(string[] arguments)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = _directory,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            return (process.ExitCode, await stdout + await stderr);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
