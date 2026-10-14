using System.Diagnostics;
using System.Xml;
using TUnit.Core;
using static DeclarationTests;

/// <summary>Builds small projects against the shipped targets and reads the policy and C# they generate.</summary>
internal sealed class PolicyGenerationTests
{
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

    [Test, DisplayName("policy: invalid declarations are build errors and write nothing")]
    public async Task RejectsInvalidDeclarationsAsync()
    {
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="Org.Example.Bad" Program="/opt/a" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.relative" Program="helper" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.no-message" Program="/opt/b" Description="d" />
            <RunicPrivilegedAction Include="org.example.authorization" Program="/opt/c" Description="d" Message="m" AllowActive="sometimes" />
            <RunicPrivilegedAction Include="org.example.newline" Program="/opt/d" Description="d" Message="first&#10;second" />
            <RunicPrivilegedAction Include="org.example.verb" Program="/opt/e" Verb="x" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.no-verb" Program="/opt/e" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.twice" Program="/opt/f" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.twice" Program="/opt/g" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.count" Program="/opt/h" Description="d" Message="m" MaximumArguments="99" />
            <RunicPrivilegedAction Include="org.example.pattern" Program="/opt/i" Description="d" Message="m" ArgumentPattern="(" />
            <RunicPrivilegedAction Include="org.example.all" Program="/opt/j" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.xml" Program="/opt/k" Verb="xml" Description="d" Message="$(BadText)" />
            <RunicPrivilegedAction Include="org.example.lookaround" Program="/opt/l" Verb="look" Description="d" Message="m" ArgumentPattern="(?=a)a" />
            """);
        var (exit, output) = await project.BuildAsync("-p:BadText=not\uFFFEXML");
        Check(exit != 0, output);
        foreach (var code in new[] { "RUNICPA002", "RUNICPA003", "RUNICPA004", "RUNICPA005", "RUNICPA006", "RUNICPA008", "RUNICPA009", "RUNICPA011", "RUNICPA012", "RUNICPA013" })
            Check(output.Contains(code, StringComparison.Ordinal), $"{code} in {output}");
        Check(!File.Exists(project.Policy) && !File.Exists(project.Source), "nothing written");
        Check(output.Contains("'org.example.xml' has a line break", StringComparison.Ordinal), "a character XML cannot carry is RUNICPA013, not a task exception");
        Check(output.Contains("'org.example.lookaround' has an ArgumentPattern that needs backtracking", StringComparison.Ordinal), "lookarounds fail at build time");
        Check(!output.Contains("MSB4018", StringComparison.Ordinal), "the task did not crash");
    }

    private static string? Text(XmlNode node, string path) => node.SelectSingleNode(path)?.InnerText;

    private static string? Annotation(XmlElement action, string key) =>
        action.SelectNodes("annotate")!.Cast<XmlElement>().FirstOrDefault(a => a.GetAttribute("key") == key)?.InnerText;

    private sealed class FixtureProject : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "runic-polkit-policy-" + Guid.NewGuid().ToString("N")[..8]);

        public FixtureProject(string items)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "fixture.proj"), $"""
                <Project>
                  <PropertyGroup>
                    <AssemblyName>Fixture</AssemblyName>
                    <RootNamespace>Example.App</RootNamespace>
                    <IntermediateOutputPath>obj/</IntermediateOutputPath>
                    <RunicPolkitPolicyName>org.example.app</RunicPolkitPolicyName>
                  </PropertyGroup>
                  <ItemGroup>
                {items}
                  </ItemGroup>
                  <Import Project="{Targets}" />
                </Project>
                """);
        }

        public string Policy => Path.Combine(_directory, "obj", "runic-privileged", "org.example.app.policy");
        public string Source => Path.Combine(_directory, "obj", "runic-privileged", "PrivilegedActions.g.cs");

        public async Task<(int ExitCode, string Output)> BuildAsync(params string[] extra)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = _directory,
            };
            string[] arguments = ["msbuild", "fixture.proj", "-t:RunicGeneratePrivilegedActions", "-nologo", "-nodeReuse:false", "-v:m", .. extra];
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
