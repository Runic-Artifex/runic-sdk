using System.Diagnostics;
using System.Xml;
using TUnit.Core;
using static Runic.Platform.MacOS.Tests.PrivilegedModelTests;

namespace Runic.Platform.MacOS.Tests;

/// <summary>Builds small projects against the shipped targets and reads the launchd property list and C# they generate.</summary>
internal sealed class PrivilegedGenerationTests
{
    private static readonly string Directory_ = Path.Combine(AppContext.BaseDirectory, "privileged-targets");

    private const string Properties = """
        <RunicMacPrivilegedDaemonLabel>org.example.app.privileged</RunicMacPrivilegedDaemonLabel>
        <RunicMacBundleIdentifier>org.example.app</RunicMacBundleIdentifier>
        <RunicMacTeamIdentifier>ABCDE12345</RunicMacTeamIdentifier>
        <RunicMacPrivilegedDaemonProgram>Contents/MacOS/example-privileged</RunicMacPrivilegedDaemonProgram>
        """;

    private const string Items = """
        <RunicPrivilegedAction Include="org.example.app.activate" Program="/usr/libexec/example/helper" Verb="activate"
                               Description="Activate" Message="Authentication is required to activate &lt;the system&gt; &amp; reboot." />
        <RunicPrivilegedAction Include="org.example.app.bundled" Program="/usr/libexec/example/helper" Verb="bundled"
                               Description="Bundled" Message="Run the bundled helper." MacProgram="Contents/MacOS/helper"
                               MacProgramIdentifier="org.example.app.helper" />
        """;

    [Test, DisplayName("generation: the launchd property list with Mach service, bundle program and launch constraint")]
    public async Task GeneratesPlistAsync()
    {
        using var project = new FixtureProject(Items, Properties);
        var (exit, output) = await project.BuildAsync();
        Check(exit == 0, output);

        var plist = new XmlDocument();
        using (var reader = XmlReader.Create(project.Plist, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            plist.Load(reader);
        Check(File.ReadAllText(project.Plist).Contains("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">", StringComparison.Ordinal), "doctype");
        var root = (XmlElement)plist.SelectSingleNode("/plist/dict")!;
        var entries = Entries(root);
        Check(entries.Keys.SequenceEqual(["Label", "BundleProgram", "MachServices", "AssociatedBundleIdentifiers", "SpawnConstraint"]), string.Join(",", entries.Keys));
        Check(entries["Label"].InnerText == "org.example.app.privileged", "label");
        Check(entries["BundleProgram"].InnerText == "Contents/MacOS/example-privileged", "bundle program");
        var services = Entries((XmlElement)entries["MachServices"]);
        Check(services.Count == 1 && services["org.example.app.privileged"].Name == "true", "the Mach service is the label");
        Check(entries["AssociatedBundleIdentifiers"].SelectNodes("string")!.Cast<XmlNode>().Select(n => n.InnerText).SequenceEqual(["org.example.app"]), "associated bundle");
        var constraint = Entries((XmlElement)entries["SpawnConstraint"]);
        Check(constraint["team-identifier"].InnerText == "ABCDE12345" && constraint["signing-identifier"].InnerText == "org.example.app.privileged", "launch constraint");
        Check(entries.Values.All(v => v.Name != "key") && !File.ReadAllText(project.Plist).Contains("UserName", StringComparison.Ordinal), "runs as root by default");

        var source = File.ReadAllText(project.MacSource);
        Check(source.Contains("namespace Example.App;", StringComparison.Ordinal) && source.Contains("internal static partial class PrivilegedActions", StringComparison.Ordinal), "a part of the shared class");
        Check(source.Contains("MacOSDaemon { get; } = new(", StringComparison.Ordinal), "MacOSDaemon");
        Check(source.Contains("\"org.example.app.privileged\", \"ABCDE12345\", \"org.example.app\", \"org.example.app.privileged\"", StringComparison.Ordinal), source);
        Check(source.Contains("new(\"org.example.app.activate\", \"Authentication is required to activate <the system> & reboot.\"),", StringComparison.Ordinal), "message");
        Check(source.Contains("{ Program = \"Contents/MacOS/helper\", ProgramIdentifier = \"org.example.app.helper\" }", StringComparison.Ordinal), "MacProgram and MacProgramIdentifier");
        Check(source.Contains("        });\n}", StringComparison.Ordinal) && !source.Contains("DevelopmentSigning", StringComparison.Ordinal), "distribution signing by default");
        Check(File.Exists(project.SharedSource), "Runic.Platform wrote the declarations after the macOS target");

        var written = File.GetLastWriteTimeUtc(project.Plist);
        await Task.Delay(50);
        (exit, output) = await project.BuildAsync();
        Check(exit == 0 && File.GetLastWriteTimeUtc(project.Plist) == written, "an unchanged property list is not rewritten");
    }

    [Test, DisplayName("generation: nothing for macOS without RunicMacPrivilegedDaemonLabel")]
    public async Task InertWithoutLabelAsync()
    {
        using var project = new FixtureProject(Items, "");
        var (exit, output) = await project.BuildAsync();
        Check(exit == 0, output);
        Check(!File.Exists(project.Plist) && !File.Exists(project.MacSource), "no macOS files");
        Check(File.Exists(project.SharedSource), "the shared declarations are still written");
    }

    [Test, DisplayName("generation: daemon settings, reserved right names and MacProgram are checked")]
    public async Task RejectsInvalidSettingsAsync()
    {
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="system.example.run" Program="/opt/a" Verb="a" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.app.up" Program="/opt/b" Verb="b" Description="d" Message="m" MacProgram="Contents/../helper" />
            <RunicPrivilegedAction Include="org.example.app.unnamed" Program="/opt/c" Verb="c" Description="d" Message="m" MacProgram="Contents/MacOS/helper" />
            """, """
            <RunicMacPrivilegedDaemonLabel>org.example.app.privileged</RunicMacPrivilegedDaemonLabel>
            <RunicMacBundleIdentifier>org.example.app</RunicMacBundleIdentifier>
            <RunicMacTeamIdentifier>abc</RunicMacTeamIdentifier>
            <RunicMacPrivilegedDaemonProgram>/Applications/Example.app/helper</RunicMacPrivilegedDaemonProgram>
            """);
        var (exit, output) = await project.BuildAsync();
        Check(exit != 0, output);
        foreach (var code in new[] { "RUNICPA101", "RUNICPA102", "RUNICPA104", "RUNICPA105" })
            Check(output.Contains(code, StringComparison.Ordinal), $"{code} in {output}");
        Check(output.Contains("RunicMacTeamIdentifier 'abc'", StringComparison.Ordinal) && output.Contains("RunicMacPrivilegedDaemonProgram", StringComparison.Ordinal), output);
        Check(!File.Exists(project.Plist) && !File.Exists(project.MacSource) && !File.Exists(project.SharedSource), "nothing written");
        Check(!output.Contains("MSB4018", StringComparison.Ordinal), "the task did not crash");
    }

    [Test, DisplayName("generation: development signing is explicit, and the daemon and a published application must be NativeAOT")]
    public async Task DevelopmentSigningAndNativeAotAsync()
    {
        using (var project = new FixtureProject(Items, Properties + "<RunicMacPrivilegedDevelopmentSigning>true</RunicMacPrivilegedDevelopmentSigning>"))
        {
            var (exit, output) = await project.BuildAsync();
            Check(exit == 0 && File.ReadAllText(project.MacSource).Contains("}) { DevelopmentSigning = true };", StringComparison.Ordinal), output);
        }
        using (var project = new FixtureProject(Items, Properties))
        {
            var (exit, output) = await project.BuildAsync("-t:RunicCheckMacPrivilegedDaemon", "-p:RunicMacPrivilegedDaemon=true", "-p:RuntimeIdentifier=osx-arm64");
            Check(exit != 0 && output.Contains("RUNICPA106", StringComparison.Ordinal), "a JIT daemon for a runtime: " + output);
            (exit, output) = await project.BuildAsync("-t:RunicCheckMacPrivilegedDaemon", "-p:RunicMacPrivilegedDaemon=true", "-p:RuntimeIdentifier=osx-arm64", "-p:PublishAot=true");
            Check(exit == 0, "a NativeAOT daemon: " + output);
            (exit, output) = await project.BuildAsync("-t:RunicCheckMacPrivilegedDaemon", "-p:RunicMacPrivilegedDaemon=true");
            Check(exit == 0, "a portable development build: " + output);
            (exit, output) = await project.BuildAsync("-t:RunicCheckMacPrivilegedPublish", "-p:RuntimeIdentifier=osx-arm64");
            Check(exit != 0 && output.Contains("RUNICPA107", StringComparison.Ordinal), "a JIT application published for macOS: " + output);
            (exit, output) = await project.BuildAsync("-t:RunicCheckMacPrivilegedPublish", "-p:RuntimeIdentifier=linux-x64");
            Check(exit == 0, "another platform: " + output);
            (exit, output) = await project.BuildAsync("-t:RunicCheckMacPrivilegedClient", "-p:RuntimeIdentifier=osx-arm64");
            Check(exit != 0 && output.Contains("RUNICPA107", StringComparison.Ordinal), "a JIT application built for macOS: " + output);
            (exit, output) = await project.BuildAsync("-t:RunicCheckMacPrivilegedClient", "-p:RuntimeIdentifier=osx-arm64", "-p:RunicMacPrivilegedDevelopmentSigning=true");
            Check(exit == 0, "development signing allows a JIT application: " + output);
            (exit, output) = await project.BuildAsync("-t:RunicCheckMacPrivilegedClient", "-p:RuntimeIdentifier=osx-arm64", "-p:PublishAot=true");
            Check(exit == 0, "a NativeAOT application: " + output);
        }
    }

    [Test, DisplayName("generation: invalid shared declarations stop the macOS target")]
    public async Task SharedValidationFirstAsync()
    {
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="Org.Bad" Program="relative" Description="d" Message="m" />
            """, Properties);
        var (exit, output) = await project.BuildAsync();
        Check(exit != 0 && output.Contains("RUNICPA004", StringComparison.Ordinal), output);
        Check(!output.Contains("RUNICPA10", StringComparison.Ordinal) && !File.Exists(project.Plist), "the macOS target did not run");
    }

    private static Dictionary<string, XmlElement> Entries(XmlElement dict)
    {
        var entries = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
        var children = dict.ChildNodes.OfType<XmlElement>().ToList();
        for (var i = 0; i + 1 < children.Count; i += 2)
        {
            Check(children[i].Name == "key", $"key expected, found {children[i].Name}");
            entries.Add(children[i].InnerText, children[i + 1]);
        }
        return entries;
    }

    private sealed class FixtureProject : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "runic-macos-privileged-" + Guid.NewGuid().ToString("N")[..8]);

        public FixtureProject(string items, string properties)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "fixture.proj"), $"""
                <Project>
                  <PropertyGroup>
                    <AssemblyName>Fixture</AssemblyName>
                    <RootNamespace>Example.App</RootNamespace>
                    <IntermediateOutputPath>obj/</IntermediateOutputPath>
                {properties}
                  </PropertyGroup>
                  <ItemGroup>
                {items}
                  </ItemGroup>
                  <Import Project="{Path.Combine(Directory_, "Runic.Platform.targets")}" />
                  <Import Project="{Path.Combine(Directory_, "Runic.Platform.MacOS.targets")}" />
                </Project>
                """);
        }

        public string Plist => Path.Combine(_directory, "obj", "runic-privileged-macos", "org.example.app.privileged.plist");
        public string MacSource => Path.Combine(_directory, "obj", "runic-privileged-macos", "PrivilegedActions.MacOS.g.cs");
        public string SharedSource => Path.Combine(_directory, "obj", "runic-privileged", "PrivilegedActions.g.cs");

        public async Task<(int ExitCode, string Output)> BuildAsync(params string[] extra)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = _directory,
            };
            string[] arguments = ["msbuild", "fixture.proj", .. extra.Any(e => e.StartsWith("-t:", StringComparison.Ordinal)) ? Array.Empty<string>() : ["-t:RunicGeneratePrivilegedActions"],
                "-nologo", "-nodeReuse:false", "-v:m", .. extra];
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
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
