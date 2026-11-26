using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// ide-run-debug K3, REPRODUCTION.md scenario M: dev runs a real Window project with its launch profile through
// dotnet run. The stand-in for the app exits 2 in a Debug build started without /mock, as an app that serves mock data
// only in development and refuses to start against a real backend from the IDE by accident does.
internal sealed partial class ToolTests
{
    private const string MockGuardProgram = """
        string arguments = string.Join(", ", args);
        #if DEBUG
        if (System.Array.IndexOf(args, "/mock") < 0)
        {
            System.Console.WriteLine($"Acme.Mock: a Debug build starts only with /mock; arguments: [{arguments}]");
            return 2;
        }
        #endif
        System.Console.WriteLine($"Acme.Mock: mock mode, arguments: [{arguments}]");
        return 0;
        """;

    /// <summary>
    /// A Window project that needs no Runic package: dev reads the Window opt-in and the frontend's package.json, and
    /// the dev session protocol of 0.24's targets, so it runs no frontend install of its own. The frontend has no
    /// scripts, so dev starts neither a development server nor a watcher.
    /// </summary>
    private static string CreateMockGuardApp(TestWorkspace workspace, string launchSettings, string startArguments = "")
    {
        string debugArguments = startArguments.Length == 0 ? string.Empty
            : $"<StartArguments Condition=\"'$(Configuration)' == 'Debug'\">{startArguments}</StartArguments>";
        string project = workspace.Write("Acme.Mock/Acme.Mock.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject>
                <_RunicViewsDevSessionProtocol>1</_RunicViewsDevSessionProtocol>
                {debugArguments}
              </PropertyGroup>
            </Project>
            """);
        workspace.Write("Acme.Mock/Program.cs", MockGuardProgram);
        workspace.Write("Acme.Mock/Frontend/package.json", """{ "name": "acme-mock-frontend", "private": true }""");
        workspace.Write("Acme.Mock/Properties/launchSettings.json", launchSettings);
        return project;
    }

    /// <summary>Runs the built tool as dev runs from a terminal, in the project's directory; standard output and error together.</summary>
    private static async Task<(int ExitCode, string Output)> RunToolAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet ? dotnet : "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(Runic.Application.Tool.Program).Assembly.Location);
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        // CI exports CONFIGURATION=Release, which MSBuild reads as a property; the cases run Debug, where the guard is.
        startInfo.Environment.Remove("CONFIGURATION");
        startInfo.Environment.Remove("Configuration");
        using Process tool = Process.Start(startInfo)!;
        Task<string> output = tool.StandardOutput.ReadToEndAsync();
        Task<string> error = tool.StandardError.ReadToEndAsync();
        try
        {
            await tool.WaitForExitAsync().WaitAsync(TestTimeouts.Scale(TimeSpan.FromMinutes(3)));
        }
        finally
        {
            if (!tool.HasExited) tool.Kill(entireProcessTree: true);
        }
        return (tool.ExitCode, await output + await error);
    }

    [Test, DisplayName("dev runs the Window with the first launch profile, and --no-launch-profile without it (K3, /mock)")]
    public async Task DevRunsTheMockProfile()
    {
        using var workspace = new TestWorkspace();
        // dotnet run reads comments; it rejects trailing commas (SDK 10.0.404).
        string project = CreateMockGuardApp(workspace, """
            {
              // The app's first profile starts the mock backend.
              "profiles": {
                "Acme.Mock": { "commandName": "Project", "commandLineArgs": "/mock", "rax": { "page": true } },
                "Acme.Real": { "commandName": "Project" }
              }
            }
            """);
        string directory = Path.GetDirectoryName(project)!;
        string[] dev = ["dev", "-c", "Debug", "--no-dotnet-watch", "--no-frontend-watch"];

        (int exitCode, string output) = await RunToolAsync(directory, dev);
        Contains(output, "[dev] Launch profile: Acme.Mock (the first Project profile; --no-launch-profile runs without one)");
        Contains(output, "[app] Acme.Mock: mock mode, arguments: [/mock]");
        True(exitCode == 0, $"dev with the profile exited with {exitCode}:\n{output}");

        (exitCode, output) = await RunToolAsync(directory, [.. dev, "--no-launch-profile"]);
        DoesNotContain(output, "[dev] Launch profile:");
        DoesNotContain(output, "mock mode");
        Contains(output, "[app] Acme.Mock: a Debug build starts only with /mock; arguments: []");
        True(exitCode != 0, $"dev without the profile exited with 0:\n{output}");
    }

    [Test, DisplayName("dev passes the project's StartArguments and the profile's arguments in order, with RAPPDEV1020 (K3, /mock)")]
    public async Task DevPassesStartArgumentsAndTheProfiles()
    {
        using var workspace = new TestWorkspace();
        string project = CreateMockGuardApp(workspace, """
            { "profiles": { "Acme.Mock": { "commandName": "Project", "commandLineArgs": "/verbose" } } }
            """, startArguments: "/mock");
        string directory = Path.GetDirectoryName(project)!;
        string[] dev = ["dev", "-c", "Debug", "--no-dotnet-watch", "--no-frontend-watch"];
        string warning = $"{Path.GetFullPath(project)} : warning RAPPDEV1020: The project sets StartArguments (/mock) and launch profile 'Acme.Mock' sets commandLineArgs (/verbose).";

        // dotnet run passes StartArguments and drops the profile's arguments; dev passes both, the project's first.
        (int exitCode, string output) = await RunToolAsync(directory, dev);
        Contains(output, warning);
        Contains(output, "[dev] Window arguments: /mock (StartArguments) /verbose (profile Acme.Mock)");
        Contains(output, "[app] Acme.Mock: mock mode, arguments: [/mock, /verbose]");
        True(exitCode == 0, $"dev exited with {exitCode}:\n{output}");

        // The arguments after -- come last.
        (exitCode, output) = await RunToolAsync(directory, [.. dev, "--", "/extra"]);
        Contains(output, warning);
        Contains(output, "[dev] Window arguments: /mock (StartArguments) /verbose (profile Acme.Mock) /extra (after --)");
        Contains(output, "[app] Acme.Mock: mock mode, arguments: [/mock, /verbose, /extra]");
        True(exitCode == 0, $"dev with -- /extra exited with {exitCode}:\n{output}");
    }
}
