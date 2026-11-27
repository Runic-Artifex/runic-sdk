using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// sdk#1405: a killed dev's frontend marker must not make the next dev's own build skip the frontend build and install.
internal sealed partial class ToolTests
{
    private static readonly DateTimeOffset DeadDevStarted = new(2026, 11, 26, 19, 0, 0, TimeSpan.Zero);

    /// <summary>A session file as dev writes it, for the owner check.</summary>
    private static void WriteSessionFile(string sessionDirectory, int processId, string state = "ready", DateTimeOffset? startedAt = null, bool withStartTime = true)
    {
        string started = (startedAt ?? DeadDevStarted).ToString("O", CultureInfo.InvariantCulture);
        string startedProperty = withStartTime ? $"\"processStartedAt\": \"{started}\"," : string.Empty;
        Write(Path.Combine(sessionDirectory, DevSession.FileName), $$"""
            { "schema": "{{DevSession.Schema}}", "state": "{{state}}", "processId": {{processId}}, {{startedProperty}} "project": "/src/App/App.csproj" }
            """);
    }

    [Test, DisplayName("a new dev removes the frontend marker of a dev that no longer runs, so its build installs the frontend (#1405)")]
    public void NewDevRemovesTheMarkerOfADeadDev()
    {
        using var workspace = new TestWorkspace();
        string targets = Path.Combine(RepositoryRoot(), "packages", "dotnet", "Runic.Application.Views", "build", "Runic.Application.Views.targets");
        string project = workspace.Write("App/App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <Import Project="{targets}" />
            </Project>
            """);
        string projectDirectory = Path.GetDirectoryName(project)!;
        string webRoot = Path.Combine(projectDirectory, "bin", "Debug", "net10.0", "www");
        string sessionDirectory = Path.Combine(projectDirectory, "obj", "runic");
        string marker = Path.Combine(sessionDirectory, DevSession.FrontendMarkerName);
        string pages = Path.Combine(sessionDirectory, DevSession.PagesDirectoryName);

        // What the build of a frontend with only a watcher decides: "false" turns off its frontend build and, since 0.24,
        // its install (RunicViewsInstallFrontend runs for a turned-off build only in the project dev names as served).
        (string Build, string Live) Detect()
        {
            CommandResult result = CommandRunner.RunAsync("dotnet", projectDirectory,
                ["msbuild", project, "-nologo", "-t:RunicViewsDetectDevSession", "-p:Configuration=Debug",
                    "-getProperty:RunicApplicationFrontendBuildEnabled,_RunicDevSessionLive"],
                CancellationToken.None).GetAwaiter().GetResult();
            True(result.ExitCode == 0, $"MSBuild failed: {result.CombinedOutput}");
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement properties = document.RootElement.GetProperty("Properties");
            return (properties.GetProperty("RunicApplicationFrontendBuildEnabled").GetString()!, properties.GetProperty("_RunicDevSessionLive").GetString()!);
        }

        // The killed dev: process 4242 has ended, or another process that started later has its ID.
        DateTimeOffset? Dead(int processId) => null;
        DateTimeOffset? Reused(int processId) => DeadDevStarted + TimeSpan.FromMinutes(3);
        IReadOnlyList<string>? NoCommandLine(int processId) => null;

        // The reproduction: a fresh marker from the killed dev, which a build within 30 s takes for a running session.
        Write(marker, webRoot + "\n");
        Write(Path.Combine(pages, "index.html"), "development page");
        WriteSessionFile(sessionDirectory, 4242);
        Equal(("false", "true"), Detect());

        string? line = DevSession.RemoveStaleFrontendMarker(sessionDirectory, Dead, NoCommandLine);
        Equal($"[dev] Removed {marker}, which a dotnet runic dev that no longer runs left (process 4242), so this dev's build installs and builds the frontend.", line);
        False(File.Exists(marker), "The dead dev's marker is still there.");
        False(Directory.Exists(pages), "The dead dev's development pages are still there.");
        True(File.Exists(Path.Combine(sessionDirectory, DevSession.FileName)), "The session file is the new dev's to replace.");
        // The new dev's build installs and builds the frontend.
        Equal(("", ""), Detect());
        Equal(null, DevSession.RemoveStaleFrontendMarker(sessionDirectory, Dead, NoCommandLine));

        // A process that reuses the ID is not the dev; nor is a session that says it stopped, or no readable session file.
        foreach ((string label, Action session, Func<int, DateTimeOffset?> startTime) in new (string, Action, Func<int, DateTimeOffset?>)[]
        {
            ("a reused process ID", () => WriteSessionFile(sessionDirectory, 4242), Reused),
            ("a stopped session", () => WriteSessionFile(sessionDirectory, 4242, "stopped"), _ => DeadDevStarted),
            ("no session file", () => File.Delete(Path.Combine(sessionDirectory, DevSession.FileName)), _ => DeadDevStarted),
            ("an unreadable session file", () => Write(Path.Combine(sessionDirectory, DevSession.FileName), "{ \"state\": "), _ => DeadDevStarted),
        })
        {
            session();
            Write(marker, webRoot + "\n");
            True(DevSession.RemoveStaleFrontendMarker(sessionDirectory, startTime, NoCommandLine) is not null, $"Nothing was removed with {label}.");
            False(File.Exists(marker), $"The marker stayed with {label}.");
        }

        // A dev that runs keeps its marker, so a build it did not start (an IDE's) keeps its frontend, as before: the
        // owner here is this test's own process, with its real start time.
        using (var current = System.Diagnostics.Process.GetCurrentProcess())
        {
            WriteSessionFile(sessionDirectory, current.Id, startedAt: new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero));
        }
        Write(marker, webRoot + "\n");
        Equal(null, DevSession.RemoveStaleFrontendMarker(sessionDirectory));
        True(File.Exists(marker), "The marker of a running dev was removed.");
        Equal(("false", "true"), Detect());
        // Start times that differ within the tolerance still name it.
        WriteSessionFile(sessionDirectory, 4242);
        Equal(null, DevSession.RemoveStaleFrontendMarker(sessionDirectory, _ => DeadDevStarted + TimeSpan.FromMilliseconds(900), NoCommandLine));
        True(File.Exists(marker), "A start time within a second was taken for another process.");

        // A session file from before 0.20 has no processStartedAt: the process's command line tells whether it is dev.
        WriteSessionFile(sessionDirectory, 4242, withStartTime: false);
        Equal(null, DevSession.RemoveStaleFrontendMarker(sessionDirectory, _ => DeadDevStarted, _ => ["/usr/bin/dotnet", "/tools/dotnet-runic.dll", "dev"]));
        True(File.Exists(marker), "The marker of a running dev from before 0.20 was removed.");
        Equal(null, DevSession.RemoveStaleFrontendMarker(sessionDirectory, _ => DeadDevStarted, NoCommandLine));
        True(File.Exists(marker), "The marker of a process whose command line cannot be read was removed.");
        True(DevSession.RemoveStaleFrontendMarker(sessionDirectory, _ => DeadDevStarted, _ => ["/usr/bin/vim"]) is not null, "Nothing was removed for a process that is not dev.");
        False(File.Exists(marker), "The marker of a process that is not dev stayed.");
    }

    [Test, DisplayName("the owner check takes the processes of this machine")]
    public void SessionOwnerCheckReadsProcesses()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        DateTimeOffset? started = DevSession.ProcessStartTime(current.Id);
        True(started is not null, "This process has no start time.");
        True((started!.Value - new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero)).Duration() < DevSession.ProcessStartTolerance,
            $"{started:O}");
        // Process IDs are positive; no process has the largest.
        Equal(null, DevSession.ProcessStartTime(int.MaxValue));
    }
}
