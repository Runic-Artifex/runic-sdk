using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// ide-run-debug K3 (D084.2, D084.3, D084.4): dev runs the Window with the first Project launch profile, as dotnet run
// does; --no-launch-profile; RAPPDEV1020 and the appended arguments; the rax object; doctor's launch-arguments.
internal sealed partial class ToolTests
{
    // Scenario M's profiles: the template's first profile, a mock profile, and one of another kind.
    private const string MockLaunchSettings = """
        {
          // dotnet run accepts comments, but not trailing commas.
          "profiles": {
            "Runic Window": { "commandName": "RunicWindow", "commandLineArgs": "/other" },
            "Acme.Mock": {
              "commandName": "Project",
              "commandLineArgs": "/mock",
              "environmentVariables": { "DOTNET_ENVIRONMENT": "Development" },
              "rax": { "dev": "no-window", "page": true, "devArguments": ["--verbosity", "minimal"] }
            },
            "Verbose": { "commandName": "Project", "commandLineArgs": "/verbose \"two words\"" },
            "Plain": { "commandName": "Project" }
          }
        }
        """;

    private static DevOptions DevDefaults(params string[] applicationArguments) =>
        new(null, "Debug", true, true, true, false, applicationArguments);

    private static (DevOptions Options, List<string> Warnings) ResolveLaunch(DevProjectConfiguration configuration, DevOptions options)
    {
        var warnings = new List<string>();
        return (DevLaunch.Resolve(configuration, options, warnings.Add), warnings);
    }

    [Test, DisplayName("a launch settings file with a trailing comma is reported, and dev uses no profile, as dotnet run does")]
    public void TrailingCommaInLaunchSettingsMeansNoProfile()
    {
        // dotnet run (SDK 10.0.404): "The launch profile "(Default)" could not be applied. … trailing comma …", then it
        // runs without the profile's arguments. dev names none, so its [dev] lines don't claim the profile.
        LaunchSettings settings = LaunchSettings.Parse("launchSettings.json",
            """{ "profiles": { "Mock": { "commandName": "Project", "commandLineArgs": "/mock", }, } }""");
        True(settings.Error is not null, "a trailing comma makes the file unreadable, as for dotnet run");
        Equal(0, settings.Profiles.Count);
        LaunchSettings withComment = LaunchSettings.Parse("launchSettings.json",
            "{\n  // a comment\n  \"profiles\": { \"Mock\": { \"commandName\": \"Project\", \"commandLineArgs\": \"/mock\" } }\n}");
        Equal(null, withComment.Error);
        Equal("/mock", withComment.FirstProject?.CommandLineArgs);
    }

    [Test, DisplayName("launch arguments are split by ProcessStartInfo.Arguments rules")]
    public void LaunchArgumentsAreSplit()
    {
        foreach ((string line, string[] expected) in new (string, string[])[]
        {
            ("", []),
            ("  ", []),
            ("/mock", ["/mock"]),
            ("/mock  \t/verbose", ["/mock", "/verbose"]),
            ("/mock \"two words\"", ["/mock", "two words"]),
            ("/config:\"C:\\Program Files\\Acme\\dev.json\"", ["/config:C:\\Program Files\\Acme\\dev.json"]),
            ("\"C:\\data\\new\"", ["C:\\data\\new"]),
            ("C:\\data\\new\\", ["C:\\data\\new\\"]),
            ("a\\\\\"b c\"", ["a\\b c"]),
            ("a\\\"b", ["a\"b"]),
            ("\"a\"\"b\"", ["a\"b"]),
            ("\"\"", [""]),
        })
        {
            SequenceEqual(expected, LaunchSettings.SplitArguments(line).ToArray());
        }
    }

    [Test, DisplayName("launchSettings.json profiles are read with comments, kinds and rax")]
    public void LaunchSettingsAreRead()
    {
        LaunchSettings settings = LaunchSettings.Parse("launchSettings.json", MockLaunchSettings);
        Equal(null, settings.Error);
        SequenceEqual(["Runic Window", "Acme.Mock", "Verbose", "Plain"], settings.Profiles.Select(static profile => profile.Name).ToArray());
        // The first Project profile, as dotnet run chooses it: the custom kind is skipped (R1).
        Equal("Acme.Mock", settings.FirstProject?.Name);
        Equal("/mock", settings.FirstProject?.CommandLineArgs);
        Equal(0, settings.FirstProject!.RaxProblems.Count);
        Equal("Verbose", settings.Find("verbose")?.Name);
        Equal(null, settings.Find("Missing"));

        LaunchSettings wrong = LaunchSettings.Parse("launchSettings.json", """
            { "profiles": { "A": { "commandName": "Project", "rax": { "dev": "always", "page": "yes", "devArguments": [1], "window": "debug" } } } }
            """);
        SequenceEqual(
            ["rax.dev must be \"no-window\" or \"off\"", "rax.page must be true or false", "rax.devArguments must be an array of strings",
                "rax.window is not a key Runic reads"],
            wrong.Profiles[0].RaxProblems.ToArray());
        Equal("\"rax\" is not an object", LaunchSettings.Parse("l.json", """{ "profiles": { "A": { "rax": [] } } }""").Profiles[0].RaxProblems.Single());
        True(LaunchSettings.Parse("l.json", "{ not json").Error is { Length: > 0 }, "Invalid JSON is an error.");
        Equal(0, LaunchSettings.Parse("l.json", "{}").Profiles.Count);
    }

    [Test, DisplayName("dev uses the first Project launch profile by default, and none with --no-launch-profile")]
    public void DevUsesTheFirstLaunchProfile()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration configuration = CreateConfiguration(workspace, "vite");

        // No launchSettings.json: no profile, as before.
        (DevOptions none, List<string> noWarnings) = ResolveLaunch(configuration, DevDefaults());
        Equal(null, none.LaunchProfile);
        Equal(0, noWarnings.Count);
        True(string.Join(" ", HostProcessController.CreateRunArguments(configuration, none)).EndsWith("--no-launch-profile", StringComparison.Ordinal), "No file, no profile.");

        workspace.Write(Path.Combine(Path.GetRelativePath(workspace.Root, configuration.ProjectDirectory), "Properties", "launchSettings.json"), MockLaunchSettings);
        (DevOptions first, List<string> warnings) = ResolveLaunch(configuration, DevDefaults());
        Equal("Acme.Mock", first.LaunchProfile);
        True(first.LaunchProfileIsDefault, "The first Project profile is the default.");
        Equal(0, warnings.Count);
        // dotnet run applies the profile's /mock itself: dev passes nothing after --.
        Equal(0, first.ProfileArguments.Count);
        string run = string.Join(" ", HostProcessController.CreateRunArguments(configuration, first));
        True(run.EndsWith("--no-build --launch-profile Acme.Mock", StringComparison.Ordinal), run);
        True(string.Join(" ", HostProcessController.CreateWatchArguments(configuration, first)).EndsWith("run --launch-profile Acme.Mock", StringComparison.Ordinal), "dotnet watch run too.");
        Equal(0, HostProcessController.ApplicationArguments(first).Count);

        // The profile's variables replace the process's, so a -e of a name the profile sets goes to dotnet run's -e,
        // which wins; other -e values stay in the environment only.
        (DevOptions staging, _) = ResolveLaunch(configuration, DevDefaults() with
        {
            Environment = [new("DOTNET_ENVIRONMENT", "Staging"), new("OTHER", "1")],
        });
        SequenceEqual(["DOTNET_ENVIRONMENT"], staging.RunEnvironment.Select(static variable => variable.Key).ToArray());
        string stagingRun = string.Join(" ", HostProcessController.CreateRunArguments(configuration, staging));
        True(stagingRun.EndsWith("--launch-profile Acme.Mock -e DOTNET_ENVIRONMENT=Staging", StringComparison.Ordinal), stagingRun);
        True(string.Join(" ", HostProcessController.CreateWatchArguments(configuration, staging)).EndsWith("run --launch-profile Acme.Mock -e DOTNET_ENVIRONMENT=Staging", StringComparison.Ordinal), "dotnet watch run takes -e too.");

        // --no-launch-profile: 0.23's behaviour.
        (DevOptions off, _) = ResolveLaunch(configuration, DevDefaults() with { UseLaunchProfile = false });
        Equal(null, off.LaunchProfile);
        Equal(0, ResolveLaunch(configuration, DevDefaults() with { UseLaunchProfile = false, Environment = [new("DOTNET_ENVIRONMENT", "Staging")] }).Options.RunEnvironment.Count);
        True(string.Join(" ", HostProcessController.CreateRunArguments(configuration, off)).EndsWith("--no-launch-profile", StringComparison.Ordinal), "--no-launch-profile.");

        // --launch-profile names one; an unknown name still goes to dotnet run, which reports it.
        Equal("Verbose", ResolveLaunch(configuration, DevDefaults() with { LaunchProfile = "Verbose" }).Options.LaunchProfile);
        Equal("Missing", ResolveLaunch(configuration, DevDefaults() with { LaunchProfile = "Missing" }).Options.LaunchProfile);
        False(ResolveLaunch(configuration, DevDefaults() with { LaunchProfile = "Verbose" }).Options.LaunchProfileIsDefault, "A named profile.");

        // --no-window leaves the profile to the IDE.
        Equal(null, ResolveLaunch(configuration, DevDefaults() with { StartWindow = false }).Options.LaunchProfile);

        // An unreadable file: a line, and no profile.
        workspace.Write(Path.Combine(Path.GetRelativePath(workspace.Root, configuration.ProjectDirectory), "Properties", "launchSettings.json"), "{ broken");
        (DevOptions broken, List<string> brokenWarnings) = ResolveLaunch(configuration, DevDefaults());
        Equal(null, broken.LaunchProfile);
        Contains(brokenWarnings.Single(), "[dev] Launch profile: none; Properties/launchSettings.json could not be read");
    }

    [Test, DisplayName("dev appends the profile's arguments to the project's and warns with RAPPDEV1020")]
    public void DevAppendsProfileArguments()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration project = CreateConfiguration(workspace, "vite");
        workspace.Write(Path.Combine(Path.GetRelativePath(workspace.Root, project.ProjectDirectory), "Properties", "launchSettings.json"), MockLaunchSettings);
        DevProjectConfiguration mock = project with { StartArguments = "/mock" };

        // StartArguments and the profile: dotnet run would drop the profile's, so dev passes them after --.
        (DevOptions both, List<string> warnings) = ResolveLaunch(mock, DevDefaults("/extra") with { LaunchProfile = "Verbose" });
        SequenceEqual(["/verbose", "two words"], both.ProfileArguments.ToArray());
        SequenceEqual(["--", "/verbose", "two words", "/extra"], HostProcessController.ApplicationArguments(both).ToArray());
        string warning = warnings.Single();
        Contains(warning, $"{mock.ProjectPath} : warning RAPPDEV1020: The project sets StartArguments (/mock) and launch profile 'Verbose' sets commandLineArgs (/verbose \"two words\").");
        Contains(warning, "dotnet runic dev, Visual Studio and Runic DevKit pass both, the project's first, but dotnet run drops the profile's.");
        Equal("[dev] Window arguments: /mock (StartArguments) /verbose \"two words\" (profile Verbose) /extra (after --)",
            DevApplication.WindowArguments(mock, both));

        // The profile and dev -- <args>: appended too, without a warning.
        (DevOptions command, List<string> none) = ResolveLaunch(project, DevDefaults("/extra"));
        SequenceEqual(["--", "/mock", "/extra"], HostProcessController.ApplicationArguments(command).ToArray());
        Equal(0, none.Count);

        // A profile without arguments, or none: nothing to append or warn about.
        (DevOptions plain, List<string> plainWarnings) = ResolveLaunch(mock, DevDefaults() with { LaunchProfile = "Plain" });
        Equal(0, plain.ProfileArguments.Count);
        Equal(0, plainWarnings.Count);
        Equal("[dev] Window arguments: /mock (StartArguments)", DevApplication.WindowArguments(mock, plain));
        Equal(0, ResolveLaunch(mock, DevDefaults() with { UseLaunchProfile = false }).Warnings.Count);
        Equal(null, DevApplication.WindowArguments(project, DevDefaults()));
    }

    [Test, DisplayName("--no-launch-profile conflicts with --launch-profile and --no-window")]
    public void NoLaunchProfileConflicts()
    {
        DevUsageException noWindow = Throws<DevUsageException>(() =>
            ToolOptionValues.RequireWindowOptions(DevDefaults() with { StartWindow = false, UseLaunchProfile = false }));
        Equal("RAPPDEV1014", noWindow.Code);
        Contains(noWindow.Message, "cannot be combined with --no-launch-profile");
        Contains(noWindow.Message, "put Debug-only arguments in the project's StartArguments");
        ToolOptionValues.RequireWindowOptions(DevDefaults() with { UseLaunchProfile = false });

        (int exitCode, string output) = RunCliHuman("dev", "--launch-profile", "Acme.Mock", "--no-launch-profile", "--project", "missing.csproj");
        Equal(2, exitCode);
        Contains(output, "--no-launch-profile");
    }

    [Test, DisplayName("doctor launch-arguments reports each surface's Window arguments")]
    public void DoctorChecksLaunchArguments()
    {
        static DoctorCheck Check(string startArguments, string? launchSettings)
        {
            var checks = new List<DoctorCheck>();
            DoctorChecks.CheckLaunchArguments(checks, startArguments,
                launchSettings is null ? null : LaunchSettings.Parse("launchSettings.json", launchSettings), "Debug");
            Equal("launch-arguments", checks.Single().Name);
            return checks.Single();
        }

        DoctorCheck nothing = Check("", null);
        Equal(DoctorStatus.Pass, nothing.Status);
        Equal("The project sets no StartArguments in Debug; the Window starts with no launch profile on every surface.", nothing.Message);
        DoctorCheck project = Check("/mock", """{ "profiles": { "Acme.Mock": { "commandName": "Project" } } }""");
        Equal(DoctorStatus.Pass, project.Status);
        Equal("Every surface starts the Window with the project's /mock in Debug, and launch profile 'Acme.Mock' without arguments.", project.Message);
        Contains(Check("", MockLaunchSettings).Message, "launch profile 'Acme.Mock' with /mock");

        DoctorCheck both = Check("/mock", MockLaunchSettings);
        Equal(DoctorStatus.Warning, both.Status);
        Contains(both.Message, "The project sets StartArguments (/mock) and launch profile 'Acme.Mock' sets commandLineArgs (/mock).");
        Contains(both.Message, "With profile 'Verbose' in Debug: dotnet run passes /mock; dotnet runic dev, Visual Studio and Runic DevKit pass /mock /verbose \"two words\".");
        DoesNotContain(both.Message, "'Runic Window'");
        Contains(both.Remediation ?? string.Empty, "Arguments for Debug builds");

        DoctorCheck rax = Check("", """{ "profiles": { "A": { "commandName": "Project", "rax": { "page": "yes" } } } }""");
        Equal(DoctorStatus.Warning, rax.Status);
        Contains(rax.Message, "A: rax.page must be true or false");

        DoctorCheck broken = Check("/mock", "{ broken");
        Equal(DoctorStatus.Warning, broken.Status);
        Contains(broken.Message, "Properties/launchSettings.json could not be read");
    }
}
