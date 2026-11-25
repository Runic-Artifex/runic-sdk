using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// dotnet runic <name> runs the extension runic-<name> (rename plan §5.1, P1): discovery, dispatch, the install hint and
// doctor's version check. The fixture extension is this test executable in its echo mode behind a script.
internal sealed partial class ToolTests
{
    private const int FixtureExitCode = 7;

    /// <summary>
    /// Writes an executable <paramref name="name"/> (a .cmd file on Windows) into <paramref name="directory"/> that
    /// records its arguments in <paramref name="output"/> and exits with <see cref="FixtureExitCode"/>.
    /// </summary>
    private static string WriteFixtureExtension(string directory, string name, string output)
    {
        Directory.CreateDirectory(directory);
        (string executable, IReadOnlyList<string> prefix) = ProcessTreeSignal.SelfCommand();
        static string Quote(string value) => "\"" + value + "\"";
        string command = string.Join(' ', [Quote(executable), .. prefix.Select(Quote), EchoArguments.Argument, Quote(output)]);
        if (OperatingSystem.IsWindows())
        {
            string batch = Path.Combine(directory, name + ".cmd");
            File.WriteAllText(batch, $"@echo off\r\n{command} %*\r\nexit /b {FixtureExitCode}\r\n");
            return batch;
        }
        string script = Path.Combine(directory, name);
        File.WriteAllText(script, $"#!/bin/sh\n{command} \"$@\"\nexit {FixtureExitCode}\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static string[] RecordedArguments(string output) => JsonSerializer.Deserialize<string[]>(File.ReadAllText(output))!;

    private static ExtensionEnvironment ExtensionEnv(
        Dictionary<string, string>? variables = null, Dictionary<string, string>? path = null, string dotnetHost = "dotnet") =>
        new(name => variables?.GetValueOrDefault(name), name => path?.GetValueOrDefault(name), dotnetHost);

    private const string TranslationsManifest =
        """{"version":1,"isRoot":true,"tools":{"dotnet-runic-translations":{"version":"0.15.1","commands":["runic-translations"]}}}""";

    [Test, DisplayName("dotnet runic translations is recognized after a leading --format, and only as the command")]
    public void ExtensionDispatchParsesTheCommand()
    {
        True(ToolExtensions.TryParseDispatch(["translations", "compile", "--project", "p.json", "--", "--x"], out KnownExtension? extension,
            out IReadOnlyList<string> leading, out IReadOnlyList<string> rest), "translations dispatches.");
        Equal<KnownExtension?>(ToolExtensions.Translations, extension);
        SequenceEqual<string>([], leading);
        SequenceEqual<string>(["compile", "--project", "p.json", "--", "--x"], rest);
        // --output is the core's hidden alias; the Translations tool reads --format.
        True(ToolExtensions.TryParseDispatch(["--output", "json", "--format=human", "--output=json", "translations", "--help"], out _, out leading, out rest),
            "translations follows a leading selector.");
        SequenceEqual<string>(["--format", "json", "--format=human", "--format=json"], leading);
        SequenceEqual<string>(["--help"], rest);
        False(ToolExtensions.TryParseDispatch(["dev", "translations"], out _, out _, out _), "translations must be the command.");
        False(ToolExtensions.TryParseDispatch(["--", "translations"], out _, out _, out _), "an application argument is not a command.");
        False(ToolExtensions.TryParseDispatch(["translation"], out _, out _, out _), "only known extensions dispatch.");
        False(ToolExtensions.TryParseDispatch([], out _, out _, out _), "no arguments run dev.");
    }

    [Test, DisplayName("extensions are found by RAX_<NAME>_TOOL, the tool manifest, node_modules/.bin and PATH, in that order")]
    public void ExtensionDiscoveryOrder()
    {
        using var workspace = new TestWorkspace();
        string nested = Path.Combine(workspace.Root, "app", "src");
        Directory.CreateDirectory(nested);
        KnownExtension translations = ToolExtensions.Translations;
        Equal("RAX_TRANSLATIONS_TOOL", ToolExtensions.EnvironmentVariable(translations));
        Equal<ResolvedExtension?>(null, ToolExtensions.Resolve(translations, nested, ExtensionEnv()));

        string global = Path.Combine(workspace.Root, "global", "runic-translations");
        var path = new Dictionary<string, string> { ["runic-translations"] = global };
        ResolvedExtension? found = ToolExtensions.Resolve(translations, nested, ExtensionEnv(path: path));
        Equal<ExtensionRoute?>(ExtensionRoute.Path, found?.Route);
        Equal(global, found!.Executable);
        SequenceEqual<string>([], found.Prefix);

        string npm = WriteFixtureExtension(Path.Combine(workspace.Root, "app", "node_modules", ".bin"), "runic-translations", "unused.json");
        found = ToolExtensions.Resolve(translations, nested, ExtensionEnv(path: path));
        Equal<ExtensionRoute?>(ExtensionRoute.NodeModules, found?.Route);
        Equal(npm, found!.Executable);

        // .NET 10's root dotnet-tools.json counts as a manifest too; the entry may name the package in any case.
        workspace.Write("dotnet-tools.json", TranslationsManifest.Replace("dotnet-runic-translations", "Dotnet-Runic-Translations", StringComparison.Ordinal));
        found = ToolExtensions.Resolve(translations, nested, ExtensionEnv(path: path, dotnetHost: "/usr/bin/dotnet"));
        Equal<ExtensionRoute?>(ExtensionRoute.Manifest, found?.Route);
        Equal("/usr/bin/dotnet", found!.Executable);
        // dotnet tool run passes options such as --help on only after --.
        SequenceEqual<string>(["tool", "run", "runic-translations", "--", "compile"], found.CommandLine(["compile"]));

        string explicitTool = Path.Combine(workspace.Root, "build", "runic-translations");
        found = ToolExtensions.Resolve(translations, nested,
            ExtensionEnv(new() { ["RAX_TRANSLATIONS_TOOL"] = explicitTool }, path));
        Equal<ExtensionRoute?>(ExtensionRoute.Environment, found?.Route);
        Equal(explicitTool, found!.Executable);
        // A relative path is the current directory's.
        found = ToolExtensions.Resolve(translations, nested, ExtensionEnv(new() { ["RAX_TRANSLATIONS_TOOL"] = "../tool" }));
        Equal(Path.Combine(workspace.Root, "app", "tool"), found!.Executable);
    }

    [Test, DisplayName("a tool manifest without the Translations tool, or above an isRoot manifest, is not used")]
    public void ExtensionDiscoveryReadsTheManifestsDotnetToolRunReads()
    {
        using var workspace = new TestWorkspace();
        string nested = Path.Combine(workspace.Root, "app");
        Directory.CreateDirectory(nested);
        workspace.Write(Path.Combine("app", ".config", "dotnet-tools.json"), """{"version":1,"isRoot":true,"tools":{"dotnet-runic":{"version":"0.24.0","commands":["dotnet-runic"]}}}""");
        workspace.Write(Path.Combine(".config", "dotnet-tools.json"), TranslationsManifest);
        Equal<ResolvedExtension?>(null, ToolExtensions.Resolve(ToolExtensions.Translations, nested, ExtensionEnv()));
        workspace.Write(Path.Combine("app", ".config", "dotnet-tools.json"), """{"version":1,"tools":{"other":{"version":"1.0.0","commands":["runic-translations"]}}}""");
        Equal<ExtensionRoute?>(ExtensionRoute.Manifest, ToolExtensions.Resolve(ToolExtensions.Translations, nested, ExtensionEnv())?.Route);
        workspace.Write(Path.Combine("app", ".config", "dotnet-tools.json"), "{ not json");
        Equal<ExtensionRoute?>(ExtensionRoute.Manifest, ToolExtensions.Resolve(ToolExtensions.Translations, nested, ExtensionEnv())?.Route);
    }

    [Test, DisplayName("dotnet runic translations runs the fixture extension from each route with the arguments unchanged")]
    public async Task ExtensionDispatchRunsTheFixtureFromEachRoute()
    {
        using var workspace = new TestWorkspace();
        string project = Path.Combine(workspace.Root, "app");
        Directory.CreateDirectory(project);
        string output = Path.Combine(workspace.Root, "arguments.json");
        string[] arguments = ["compile", "--project", "with space/runic.json", "--format", "json", "--", "--help"];

        async Task<string[]> Dispatch(ExtensionEnvironment environment, ExtensionRoute route)
        {
            if (File.Exists(output)) File.Delete(output);
            ResolvedExtension resolved = ToolExtensions.Resolve(ToolExtensions.Translations, project, environment)
                ?? throw new InvalidOperationException($"The {route} fixture was not found.");
            Equal(route, resolved.Route);
            Equal(FixtureExitCode, await ToolExtensions.RunAsync(resolved, arguments, project));
            return RecordedArguments(output);
        }

        string onPath = WriteFixtureExtension(Path.Combine(workspace.Root, "path"), "runic-translations", output);
        SequenceEqual<string>(arguments, await Dispatch(ExtensionEnv(path: new() { ["runic-translations"] = onPath }), ExtensionRoute.Path));

        WriteFixtureExtension(Path.Combine(project, "node_modules", ".bin"), "runic-translations", output);
        SequenceEqual<string>(arguments, await Dispatch(ExtensionEnv(), ExtensionRoute.NodeModules));

        // The manifest route runs dotnet tool run; the fixture stands in for dotnet and records that command line.
        string dotnet = WriteFixtureExtension(Path.Combine(workspace.Root, "sdk"), "dotnet", output);
        workspace.Write(Path.Combine("app", ".config", "dotnet-tools.json"), TranslationsManifest);
        SequenceEqual<string>(["tool", "run", "runic-translations", "--", .. arguments], await Dispatch(ExtensionEnv(dotnetHost: dotnet), ExtensionRoute.Manifest));

        string explicitTool = WriteFixtureExtension(Path.Combine(workspace.Root, "explicit"), "translations-build", output);
        SequenceEqual<string>(arguments, await Dispatch(ExtensionEnv(new() { ["RAX_TRANSLATIONS_TOOL"] = explicitTool }), ExtensionRoute.Environment));

        // A path that does not start reports the route and exits 1 instead of throwing.
        ResolvedExtension missing = ToolExtensions.Resolve(ToolExtensions.Translations, project,
            ExtensionEnv(new() { ["RAX_TRANSLATIONS_TOOL"] = Path.Combine(workspace.Root, "nowhere") }))!;
        Equal(1, await ToolExtensions.RunAsync(missing, arguments, project));
    }

    [Test, DisplayName("Program dispatches dotnet runic translations to RAX_TRANSLATIONS_TOOL and returns its exit code")]
    public async Task ProgramDispatchesToTheExtension()
    {
        using var workspace = new TestWorkspace();
        string output = Path.Combine(workspace.Root, "arguments.json");
        string fixture = WriteFixtureExtension(Path.Combine(workspace.Root, "tool"), "runic-translations", output);
        string? previous = System.Environment.GetEnvironmentVariable("RAX_TRANSLATIONS_TOOL");
        try
        {
            // The cases run one at a time (AssemblyInfo.cs), so the process environment is this case's.
            System.Environment.SetEnvironmentVariable("RAX_TRANSLATIONS_TOOL", fixture);
            Equal(FixtureExitCode, await Runic.Application.Tool.Program.RunAsync(["--output", "json", "translations", "status", "--strict"]));
            SequenceEqual<string>(["--format", "json", "status", "--strict"], RecordedArguments(output));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("RAX_TRANSLATIONS_TOOL", previous);
        }
    }

    [Test, DisplayName("without the extension, dotnet runic translations prints the install command")]
    public void MissingExtensionPrintsTheInstallCommand()
    {
        using var workspace = new TestWorkspace();
        string version = Train(CompatibilitySetAuthority.Current, "Translations").Version;
        string message = ToolExtensions.MissingMessage(ToolExtensions.Translations, version, workspace.Root);
        Contains(message, $"'dotnet tool install dotnet-runic-translations --version {version}'");
        Contains(message, "RAX_TRANSLATIONS_TOOL");
        DoesNotContain(message, "--local");
        False(Runic.Application.Tool.Program.ContainsRootedPath(message), "The hint must not be replaced by the private-path notice.");
        // With a tool manifest, the tool is installed into it, so dotnet tool restore brings it back.
        workspace.Write(Path.Combine(".config", "dotnet-tools.json"), """{"version":1,"isRoot":true,"tools":{}}""");
        Contains(ToolExtensions.MissingMessage(ToolExtensions.Translations, version, Path.Combine(workspace.Root, "src")),
            $"dotnet tool install dotnet-runic-translations --version {version} --local");

        var outcome = Runic.Application.Tool.Program.Translations();
        False(outcome.Succeeded, "translations without the tool fails.");
        Equal(Runic.CommandLine.CommandExitCategory.Unavailable, outcome.ExitCategory);
        Equal(ToolExtensions.MissingCode, outcome.Fault!.Code);
        Contains(outcome.Fault.Message, $"--version {version}");
        Runic.CommandLine.CommandCatalog catalog = Runic.Application.Tool.Program.CreateCatalog();
        False(catalog.Commands.Single(command => command.Name == "translations").Help.Hidden, "help lists translations.");
    }

    [Test, DisplayName("--extension-info is one JSON object with name, version and extensionProtocol")]
    public void ExtensionInfoParses()
    {
        Equal<ExtensionInfo?>(new ExtensionInfo("translations", "0.16.0", 1),
            ToolExtensions.ParseInfo("""{"name":"translations","version":"0.16.0","extensionProtocol":1,"command":"runic-translations"}"""));
        // dotnet tool run may print a notice first.
        Equal<ExtensionInfo?>(new ExtensionInfo("translations", "0.16.0-preview.1", 1),
            ToolExtensions.ParseInfo("Welcome to .NET!\n{\"name\":\"translations\",\"version\":\"0.16.0-preview.1\",\"extensionProtocol\":1}\n"));
        Equal<ExtensionInfo?>(null, ToolExtensions.ParseInfo("""{"name":"translations","version":"0.16.0"}"""));
        Equal<ExtensionInfo?>(null, ToolExtensions.ParseInfo("Unknown option '--extension-info'."));
        Equal<ExtensionInfo?>(new ExtensionInfo("translations", "0.15.1", null), ToolExtensions.ParseVersion(ToolExtensions.Translations, "0.15.1+0123abc\n"));
        // The Translations tool's --version names itself first.
        Equal<ExtensionInfo?>(new ExtensionInfo("translations", "0.15.1", null), ToolExtensions.ParseVersion(ToolExtensions.Translations, "runic-translations 0.15.1\n"));
        Equal<ExtensionInfo?>(null, ToolExtensions.ParseVersion(ToolExtensions.Translations, "runic-translations"));
    }

    [Test, DisplayName("doctor checks the installed extension's version and protocol against the compatibility set")]
    public void DoctorChecksExtensionVersions()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration project = CreateDoctorProject(workspace, "Runic.Application.Views.Desktop", out CompatibilitySetAuthority authority);
        string train = Train(authority, "Translations").Version;
        Version parsed = Version.Parse(train);
        string otherLine = parsed.Major == 0 ? $"0.{parsed.Minor - 1}.3" : $"{parsed.Major - 1}.0.0";
        string olderPatch = parsed.Build > 0 ? $"{parsed.Major}.{parsed.Minor}.{parsed.Build - 1}" : $"{train}-preview.1";
        string laterPatch = $"{parsed.Major}.{parsed.Minor}.{parsed.Build + 1}";
        const string Tool = "runic-translations";

        DoctorReport Inspect(Func<string, IReadOnlyList<string>, CommandResult?>? commands, bool installed = true, Dictionary<string, string>? environment = null) =>
            DoctorChecks.InspectAsync(project, "dotnet", new FakeDoctorRuntime(authority.Toolchain)
            {
                Executables = installed ? [Tool] : [],
                Environment = environment ?? [],
                Commands = commands,
            }, CancellationToken.None).GetAwaiter().GetResult();
        Func<string, IReadOnlyList<string>, CommandResult?> Info(string version, int protocol = 1, string name = "translations") =>
            (executable, arguments) => executable == Tool && arguments.SequenceEqual<string>(["--extension-info"])
                ? new CommandResult(0, JsonSerializer.Serialize(new { name, version, extensionProtocol = protocol }) + "\n", string.Empty)
                : null;

        // PATH has it under its command name; FakeDoctorRuntime finds names in Executables.
        DoctorCheck none = Check(Inspect(null, installed: false), "extensions");
        Equal(DoctorStatus.Pass, none.Status);
        Contains(none.Message, "No dotnet runic tool extension is installed");

        // A tool that predates --extension-info still names its release with --version.
        Func<string, IReadOnlyList<string>, CommandResult?> before = (executable, arguments) => executable != Tool ? null
            : arguments.SequenceEqual<string>(["--extension-info"]) ? new CommandResult(2, string.Empty, "Unknown option '--extension-info'.")
            : arguments.SequenceEqual<string>(["--version"]) ? new CommandResult(0, train + "+0123abc\n", string.Empty) : null;
        DoctorCheck predates = Check(InspectOnPath(before), "extensions");
        Equal(DoctorStatus.Pass, predates.Status);
        Contains(predates.Message, $"runic-translations (PATH) {train}");

        Equal(DoctorStatus.Pass, Check(InspectOnPath(Info(laterPatch)), "extensions").Status);

        DoctorCheck older = Check(InspectOnPath(Info(olderPatch)), "extensions");
        Equal(DoctorStatus.Warning, older.Status);
        Contains(older.Remediation!, $"dotnet tool update dotnet-runic-translations --version {train}");

        DoctorReport mismatch = InspectOnPath(Info(otherLine));
        DoctorCheck line = Check(mismatch, "extensions");
        Equal(DoctorStatus.Failure, line.Status);
        False(mismatch.IsHealthy, "Another release line fails doctor.");
        Contains(line.Message, $"runic-translations (PATH) {otherLine} is older than the");
        Contains(line.Remediation!, $"dotnet tool update dotnet-runic-translations --version {train}");

        // Translations releases between trains, so a later line only warns, as the compatibility-set check does.
        string laterLine = parsed.Major == 0 ? $"0.{parsed.Minor + 1}.0" : $"{parsed.Major + 1}.0.0";
        DoctorCheck later = Check(InspectOnPath(Info(laterLine)), "extensions");
        Equal(DoctorStatus.Warning, later.Status);
        Contains(later.Message, "of a later line");

        DoctorCheck protocol = Check(InspectOnPath(Info(train, protocol: 2)), "extensions");
        Equal(DoctorStatus.Failure, protocol.Status);
        Contains(protocol.Message, "extension protocol 2");
        Equal(DoctorStatus.Failure, Check(InspectOnPath(Info(train, name: "other")), "extensions").Status);

        DoctorCheck silent = Check(InspectOnPath((_, _) => new CommandResult(1, string.Empty, "crashed")), "extensions");
        Equal(DoctorStatus.Warning, silent.Status);

        // From a tool manifest, doctor asks dotnet tool run, as dispatch runs it, and suggests updating it there.
        workspace.Write(Path.Combine(".config", "dotnet-tools.json"), TranslationsManifest);
        DoctorCheck manifest = Check(Inspect((executable, arguments) =>
            executable == "dotnet" && arguments.SequenceEqual<string>(["tool", "run", "runic-translations", "--", "--extension-info"])
                ? new CommandResult(0, JsonSerializer.Serialize(new { name = "translations", version = otherLine, extensionProtocol = 1 }), string.Empty)
                : null, installed: false), "extensions");
        Equal(DoctorStatus.Failure, manifest.Status);
        Contains(manifest.Message, "runic-translations (the tool manifest)");
        Contains(manifest.Remediation!, $"--version {train} --local");

        DoctorReport InspectOnPath(Func<string, IReadOnlyList<string>, CommandResult?> commands) => Inspect(commands);
    }
}
