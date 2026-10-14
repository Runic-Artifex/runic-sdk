using System.Diagnostics;
using Runic.Platform.Windows.Elevation;
using TUnit.Core;
using static Runic.Elevation.Smoke.Checks;

namespace Runic.Elevation.Smoke;

/// <summary>Builds small projects against the shipped targets: the WindowsElevation part of the class and the helper checks.</summary>
internal sealed class GenerationTests
{
    private static readonly string PlatformTargets = Path.Combine(AppContext.BaseDirectory, "platform-targets", "Runic.Platform.targets");
    private static readonly string ElevationTargets = Path.Combine(AppContext.BaseDirectory, "elevation-targets", "Runic.Platform.Windows.Elevation.targets");

    [Test, DisplayName("generation: WindowsElevation carries each action's WindowsProgram")]
    public async Task GeneratesWindowsElevationAsync()
    {
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="org.example.app.activate" Program="/opt/example/helper" Verb="activate" WindowsProgram="helpers\example helper's.exe"
                                   Description="Activate" Message="m" />
            <RunicPrivilegedAction Include="org.example.app.collect" Program="/opt/example/helper" Verb="collect" Description="Collect" Message="m" />
            """);
        var (exit, output) = await project.BuildAsync("-t:RunicGeneratePrivilegedActions");
        Check(exit == 0, output);
        var source = File.ReadAllText(Path.Combine(project.Directory, "obj", "runic-privileged", "PrivilegedActions.Windows.g.cs"));
        Check(source.Contains("namespace Example.App;", StringComparison.Ordinal) && source.Contains("internal static partial class PrivilegedActions", StringComparison.Ordinal), source);
        Check(source.Contains("""["org.example.app.activate"] = "helpers\\example helper's.exe",""", StringComparison.Ordinal), source);
        Check(!source.Contains("org.example.app.collect", StringComparison.Ordinal), "an action without WindowsProgram uses the default");
        Check(File.Exists(Path.Combine(project.Directory, "obj", "runic-privileged", "PrivilegedActions.g.cs")), "the shared declarations are written too");
        // The generated value of this project's own items.
        Check(PrivilegedActions.WindowsElevation.Programs is { Count: 0 }, "the smoke's actions use the default location");
    }

    [Test, DisplayName("generation: a WindowsProgram outside the application or not a local path is RUNICEL001")]
    public async Task RejectsWindowsProgramAsync()
    {
        using var project = new FixtureProject("""
            <RunicPrivilegedAction Include="org.example.app.up" Program="/opt/example/helper" Verb="up" WindowsProgram="..\helper.exe" Description="d" Message="m" />
            <RunicPrivilegedAction Include="org.example.app.share" Program="/opt/example/helper" Verb="share" WindowsProgram="\\server\share\helper.exe" Description="d" Message="m" />
            """);
        var (exit, output) = await project.BuildAsync("-t:RunicGeneratePrivilegedActions");
        Check(exit != 0 && output.Split('\n').Count(l => l.Contains("error RUNICEL001", StringComparison.Ordinal)) == 2, output);
        Check(!File.Exists(Path.Combine(project.Directory, "obj", "runic-privileged", "PrivilegedActions.g.cs")), "nothing written");
        foreach (var valid in new[] { @"helpers\x.exe", "x.exe", @"C:\Program Files\X\x.exe" })
            Check(HelperLocation.IsValidProgram(valid), valid);
    }

    [Test, DisplayName("helper: NativeAOT for deployable builds, a requireAdministrator manifest and a console executable")]
    public async Task ChecksHelperAsync()
    {
        using (var project = new FixtureProject("", "<RunicElevatedHelper>true</RunicElevatedHelper><OutputType>Library</OutputType><RuntimeIdentifier>win-x64</RuntimeIdentifier><IlcStandaloneGCSupport>true</IlcStandaloneGCSupport><EventSourceSupport>true</EventSourceSupport>"))
        {
            var (exit, output) = await project.BuildAsync("-t:RunicCheckElevatedHelper");
            Check(exit != 0 && output.Contains("RUNICEL002", StringComparison.Ordinal) && output.Contains("RUNICEL004", StringComparison.Ordinal)
                && !output.Contains("RUNICEL003", StringComparison.Ordinal), $"the package's manifest, no NativeAOT for a RID, a library: {output}");
            var pinned = await project.EvaluateAsync("IlcStandaloneGCSupport", "EventSourceSupport");
            Check(pinned.Contains("\"IlcStandaloneGCSupport\": \"false\"", StringComparison.Ordinal) && pinned.Contains("\"EventSourceSupport\": \"false\"", StringComparison.Ordinal),
                $"the standalone GC and EventPipe are off: {pinned}");
            var items = await project.EvaluateItemsAsync("RuntimeHostConfigurationOption", "LinkerArg");
            Check(items.Contains("System.Globalization.UseNls", StringComparison.Ordinal) && items.Contains("/DEPENDENTLOADFLAG:0x800", StringComparison.Ordinal), $"NLS and the load flag: {items}");
            var manifest = await project.EvaluateAsync("ApplicationManifest");
            Check(manifest.EndsWith("Runic.Platform.Windows.Elevation.manifest", StringComparison.Ordinal), manifest);
            Check(File.ReadAllText(manifest).Contains("level=\"requireAdministrator\"", StringComparison.Ordinal), "the package's manifest requires administrator");
        }
        using (var project = new FixtureProject("", "<RunicElevatedHelper>true</RunicElevatedHelper><OutputType>Exe</OutputType><PublishAot>true</PublishAot><ApplicationManifest>app.manifest</ApplicationManifest>"))
        {
            File.WriteAllText(Path.Combine(project.Directory, "app.manifest"), """<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"><trustInfo><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" /></requestedPrivileges></security></trustInfo></assembly>""");
            var (exit, output) = await project.BuildAsync("-t:RunicCheckElevatedHelper");
            Check(exit != 0 && output.Contains("RUNICEL003", StringComparison.Ordinal) && !output.Contains("RUNICEL002", StringComparison.Ordinal), $"an asInvoker manifest: {output}");
        }
        using (var project = new FixtureProject("", "<RunicElevatedHelper>true</RunicElevatedHelper><OutputType>Exe</OutputType>"))
        {
            var (exit, output) = await project.BuildAsync("-t:RunicCheckElevatedHelper");
            Check(exit == 0, $"a portable development build: {output}");
            (exit, output) = await project.BuildAsync("-t:RunicCheckElevatedHelperPublish");
            Check(exit != 0 && output.Contains("RUNICEL002", StringComparison.Ordinal), $"publishing without NativeAOT: {output}");
        }
        using (var project = new FixtureProject("", "<RunicElevatedHelper>true</RunicElevatedHelper><OutputType>Exe</OutputType><PublishAot>true</PublishAot><RuntimeIdentifier>win-x64</RuntimeIdentifier>"))
        {
            var (exit, output) = await project.BuildAsync("-t:RunicCheckElevatedHelper;RunicCheckElevatedHelperPublish");
            Check(exit == 0, $"a NativeAOT helper: {output}");
        }
    }

    private sealed class FixtureProject : IDisposable
    {
        public FixtureProject(string items, string properties = "")
        {
            Directory = Path.Combine(Path.GetTempPath(), "runic-elevation-targets-" + Guid.NewGuid().ToString("N")[..8]);
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, "fixture.proj"), $"""
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
                  <Import Project="{PlatformTargets}" />
                  <Import Project="{ElevationTargets}" />
                </Project>
                """);
        }

        public string Directory { get; }

        public async Task<string> EvaluateAsync(params string[] properties)
        {
            var (exit, output) = await RunAsync(["msbuild", "fixture.proj", "-nologo", .. properties.Select(p => $"-getProperty:{p}")]);
            Check(exit == 0, output);
            return output.Trim();
        }

        public async Task<string> EvaluateItemsAsync(params string[] items)
        {
            var (exit, output) = await RunAsync(["msbuild", "fixture.proj", "-nologo", .. items.Select(i => $"-getItem:{i}")]);
            Check(exit == 0, output);
            return output;
        }

        public Task<(int ExitCode, string Output)> BuildAsync(params string[] extra) =>
            RunAsync(["msbuild", "fixture.proj", "-nologo", "-nodeReuse:false", "-v:m", .. extra]);

        private async Task<(int ExitCode, string Output)> RunAsync(string[] arguments)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Directory,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            return (process.ExitCode, await stdout + await stderr);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
