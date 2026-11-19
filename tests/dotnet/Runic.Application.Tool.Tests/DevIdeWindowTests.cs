using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

/// <summary><c>dotnet runic dev --no-window</c> (#960): the frontend for a Window that an IDE starts under its debugger.</summary>
internal sealed partial class ToolTests
{
    [Test, DisplayName("--no-window rejects the options that only describe how dev starts the Window")]
    public void NoWindowRejectsWindowOptions()
    {
        var frontendOnly = new DevOptions(null, "Debug", true, true, true, false, []) { StartWindow = false };
        ToolOptionValues.RequireWindowOptions(frontendOnly);
        ToolOptionValues.RequireWindowOptions(frontendOnly with { StartWindow = true, WatchHost = false });

        DevUsageException rejected = Throws<DevUsageException>(() => ToolOptionValues.RequireWindowOptions(frontendOnly with
        {
            WatchFrontend = false,
            WatchHost = false,
            LaunchProfile = "Development",
            Environment = [new("A", "1")],
            ApplicationArguments = ["--flag"],
        }));
        Equal(ToolOptionValues.NoWindowConflictCode, rejected.Code);
        foreach (string option in new[] { "--no-frontend-watch", "--no-dotnet-watch", "--launch-profile", "-e", "application arguments" })
        {
            Contains(rejected.Message, option);
        }
        Equal(false, Runic.Application.Tool.Program.ContainsRootedPath(rejected.Message));
    }

    [Test, DisplayName("--no-window needs a development server or a frontend watcher")]
    public void NoWindowNeedsAServedFrontend()
    {
        using var workspace = new TestWorkspace();
        var frontendOnly = new DevOptions(null, "Debug", true, true, true, false, []) { StartWindow = false };
        DevApplication.RequireWindowFrontend(CreateConfiguration(workspace, "vite"), frontendOnly);
        DevApplication.RequireWindowFrontend(CreateConfiguration(workspace, string.Empty) with { FrontendWatchTarget = "WatchFrontend" }, frontendOnly);

        // An external compiler or static assets: nothing for dev to run.
        DevProjectConfiguration none = CreateConfiguration(workspace, string.Empty) with { Workspace = string.Empty };
        DevApplication.RequireWindowFrontend(none, frontendOnly with { StartWindow = true });
        DevDevelopmentException failed = Throws<DevDevelopmentException>(() => DevApplication.RequireWindowFrontend(none, frontendOnly));
        Equal(ToolOptionValues.NoWindowFrontendCode, failed.Code);
        Equal(Runic.CommandLine.CommandExitCategory.CommandFailure, failed.Category);
    }

    [Test, DisplayName("a failing --no-window prints its diagnostic line before evaluating the project")]
    public async Task NoWindowConflictIsADiagnosticLine()
    {
        var console = new CapturingConsole();
        int exitCode = await Runic.Application.Tool.Program.RunAsync(["dev", "--no-window", "--no-dotnet-watch", "--project", "missing.csproj"], console);
        Equal(Runic.Application.Tool.Program.UsageFailure, exitCode);
        Contains(console.Error + console.Output, "dotnet-runic : error RAPPDEV1014: --no-window runs only the frontend");
    }

    [Test, DisplayName("the session tells the IDE's Window what it needs and marks the frontend for the build")]
    public async Task NoWindowSessionPublishesTheFrontend()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration configuration = CreateConfiguration(workspace, "vite") with
        {
            DevelopmentServerDocument = "index.html;pages/settings.html",
        };
        var options = new DevOptions(null, "Debug", true, true, true, false, []) { StartWindow = false };
        var session = new DevSession(configuration, options, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        Write(Path.Combine(configuration.RuntimeWebRoot, "index.html"), "<script src=\"http://127.0.0.1:5173/@vite/client\"></script>");
        Write(Path.Combine(configuration.RuntimeWebRoot, "pages", "settings.html"), "settings");
        // A page left from an earlier session that no longer has it.
        Write(Path.Combine(session.PagesDirectory, "old.html"), "old");

        session.Start();
        session.FrontendReady(new Uri("http://127.0.0.1:5173/"), confirmsSession: false);
        session.PublishFrontend(configuration.DevelopmentServerDocuments);
        Equal(configuration.RuntimeWebRoot, File.ReadAllText(session.FrontendMarkerPath).Trim());
        Equal(Path.Combine(configuration.SessionDirectory, DevSession.FrontendMarkerName), session.FrontendMarkerPath);
        Contains(File.ReadAllText(Path.Combine(session.PagesDirectory, "index.html")), "@vite/client");
        Equal("settings", File.ReadAllText(Path.Combine(session.PagesDirectory, "pages", "settings.html")));
        False(File.Exists(Path.Combine(session.PagesDirectory, "old.html")), "A page of an earlier session was kept.");

        using (JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(session.Path)))
        {
            JsonElement window = document.RootElement.GetProperty("window");
            Equal("ide", window.GetProperty("owner").GetString());
            Equal(JsonValueKind.Null, window.GetProperty("processId").ValueKind);
            JsonElement environment = window.GetProperty("environment");
            Equal("Development", environment.GetProperty("DOTNET_ENVIRONMENT").GetString());
            // The development page names the server, so the Window needs nothing else.
            False(environment.TryGetProperty(HostProcessController.DevelopmentAssetsVariable, out _), "A served frontend was given the live assets directory.");
        }

        // dev renews the marker, so the build can tell a running session from one that was killed.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        File.SetLastWriteTimeUtc(session.FrontendMarkerPath, DateTime.UtcNow - TimeSpan.FromMinutes(5));
        using var stop = new CancellationTokenSource();
        Task renewal = session.RenewFrontendAsync(time, stop.Token);
        time.Advance(DevSession.FrontendHeartbeat);
        var deadline = Stopwatch.StartNew();
        while (DateTime.UtcNow - File.GetLastWriteTimeUtc(session.FrontendMarkerPath) > DevSession.FrontendMarkerLifetime &&
            deadline.Elapsed < TestTimeouts.Scale(TimeSpan.FromSeconds(10)))
        {
            await Task.Delay(20);
        }
        True(DateTime.UtcNow - File.GetLastWriteTimeUtc(session.FrontendMarkerPath) < DevSession.FrontendMarkerLifetime, "The marker was not renewed.");
        stop.Cancel();
        await renewal.WaitAsync(TestTimeouts.Scale(TimeSpan.FromSeconds(10)));

        // When dev ends, builds run the frontend build and copy again, and the stop stamp, which Visual Studio's fast
        // up-to-date check reads (WIN-01), is newer than the last build.
        Write(session.StoppedStampPath, string.Empty);
        File.SetLastWriteTimeUtc(session.StoppedStampPath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        DateTime beforeStop = DateTime.UtcNow - TimeSpan.FromSeconds(2);
        // A build replaced one page meanwhile; the other is still dev's.
        Write(Path.Combine(configuration.RuntimeWebRoot, "pages", "settings.html"), "built");
        session.Stop();
        False(File.Exists(session.FrontendMarkerPath), "The frontend marker outlived the session.");
        False(Directory.Exists(session.PagesDirectory), "The development pages outlived the session.");
        // DEV-SEC-48 (#1178): dev's page no longer loads script from the stopped server's port, which another process
        // could take; a page that a build wrote is left as it is.
        string retired = File.ReadAllText(Path.Combine(configuration.RuntimeWebRoot, "index.html"));
        Equal(DevSession.StoppedPage, retired);
        DoesNotContain(retired, "127.0.0.1");
        Contains(retired, "default-src 'none'");
        Equal("built", File.ReadAllText(Path.Combine(configuration.RuntimeWebRoot, "pages", "settings.html")));
        True(File.Exists(session.Path), "The stopped session file was removed.");
        Equal(Path.Combine(configuration.SessionDirectory, DevSession.StoppedStampName), session.StoppedStampPath);
        True(File.GetLastWriteTimeUtc(session.StoppedStampPath) >= beforeStop, "dev did not touch the stop stamp.");

        // Without a development server, a Runic Desktop Window serves the watcher's output live, as under dev.
        DevProjectConfiguration watched = CreateConfiguration(workspace, string.Empty) with { ApplicationHosts = ["desktop"] };
        IReadOnlyList<KeyValuePair<string, string>> variables = DevSession.WindowEnvironment(watched, options);
        Equal(Path.GetFullPath(watched.FrontendOutputDirectory), variables.Single(variable => variable.Key == HostProcessController.DevelopmentAssetsVariable).Value);
        Equal(1, DevSession.WindowEnvironment(watched with { ApplicationHosts = ["cswebui"] }, options).Count);
    }

    [Test, DisplayName("a build during a live dev session keeps the development pages, and only then")]
    public void BuildKeepsDevSessionPages()
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

        (string Build, string Copy, string Live) Detect(params string[] extra)
        {
            CommandResult result = CommandRunner.RunAsync("dotnet", projectDirectory,
                // Debug unless a case says otherwise: CI exports CONFIGURATION=Release, which MSBuild reads as a property.
                ["msbuild", project, "-nologo", "-t:RunicViewsDetectDevSession;RunicViewsRestoreDevSessionPages", "-p:Configuration=Debug",
                    "-getProperty:RunicApplicationFrontendBuildEnabled,RunicApplicationFrontendCopyEnabled,_RunicDevSessionLive", .. extra],
                CancellationToken.None).GetAwaiter().GetResult();
            True(result.ExitCode == 0, $"MSBuild failed: {result.CombinedOutput}");
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement properties = document.RootElement.GetProperty("Properties");
            return (properties.GetProperty("RunicApplicationFrontendBuildEnabled").GetString()!,
                properties.GetProperty("RunicApplicationFrontendCopyEnabled").GetString()!,
                properties.GetProperty("_RunicDevSessionLive").GetString()!);
        }

        // No session: the build is unchanged.
        Equal(("", "", ""), Detect());

        // A session with a development server: no frontend build, no copy over the pages, which come back after a clean.
        Write(marker, webRoot + "\n");
        Write(Path.Combine(pages, "index.html"), "development page");
        Equal(("false", "false", "true"), Detect());
        Equal("development page", File.ReadAllText(Path.Combine(webRoot, "index.html")));

        // Only for the output dev serves, never for publish, and not when turned off.
        Equal(("", "", ""), Detect("-p:Configuration=Release"));
        Equal(("", "", ""), Detect("-p:_IsPublishing=true"));
        Equal(("", "", ""), Detect("-p:RunicApplicationFrontendDevSessionEnabled=false"));

        // A frontend watcher: the watcher builds the frontend, the build still copies its output.
        Directory.Delete(pages, recursive: true);
        Equal(("false", "", "true"), Detect());

        // The marker of a dev that was killed ages out.
        File.SetLastWriteTime(marker, DateTime.Now - DevSession.FrontendMarkerLifetime - TimeSpan.FromSeconds(5));
        Equal(("", "", ""), Detect());
    }
}
