using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// devtools-library-dx S7 (F9): dev and doctor from a solution root pick the solution's one Window project.
internal sealed partial class ToolTests
{
    /// <summary>A solution root with an app, a library and tests; <paramref name="windows"/> are the Window projects.</summary>
    private static TestWorkspace CreateSolutionRoot(string solutionFile, params string[] windows)
    {
        var workspace = new TestWorkspace();
        foreach (string project in new[] { "Acme.App/Acme.App.csproj", "Acme.Core/Acme.Core.csproj", "Acme.Tests/Acme.Tests.csproj", "Acme.Admin/Acme.Admin.csproj" })
        {
            workspace.Write(project, windows.Contains(Path.GetFileNameWithoutExtension(project))
                ? "<Project><PropertyGroup><RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject></PropertyGroup></Project>"
                : "<Project />");
        }
        workspace.Write(solutionFile, solutionFile.EndsWith(".slnx", StringComparison.Ordinal)
            ? """
              <Solution>
                <Project Path="Acme.App/Acme.App.csproj" />
                <Project Path="Acme.Core\Acme.Core.csproj" />
                <Folder Name="/tests/">
                  <Project Path="Acme.Tests/Acme.Tests.csproj" />
                </Folder>
                <Project Path="Acme.Admin/Acme.Admin.csproj" />
                <Project Path="Missing/Missing.csproj" />
              </Solution>
              """
            : """
              Microsoft Visual Studio Solution File, Format Version 12.00
              Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Acme.App", "Acme.App\Acme.App.csproj", "{11111111-1111-1111-1111-111111111111}"
              EndProject
              Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "tests", "tests", "{22222222-2222-2222-2222-222222222222}"
              EndProject
              Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Acme.Core", "Acme.Core\Acme.Core.csproj", "{33333333-3333-3333-3333-333333333333}"
              EndProject
              Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Acme.Tests", "Acme.Tests\Acme.Tests.csproj", "{44444444-4444-4444-4444-444444444444}"
              EndProject
              Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Acme.Admin", "Acme.Admin\Acme.Admin.csproj", "{55555555-5555-5555-5555-555555555555}"
              EndProject
              """);
        return workspace;
    }

    // Reads the project file as MSBuild would for this one property, so the tests need no MSBuild.
    private static Task<bool?> ReadWindowFlag(string project, CancellationToken cancellationToken) =>
        Task.FromResult<bool?>(File.ReadAllText(project).Contains("<RunicApplicationFrontendWindowProject>true<", StringComparison.Ordinal));

    private static ProjectSelection FindWindowProject(string directory, string? requested = null,
        Func<string, CancellationToken, Task<bool?>>? probe = null) =>
        ProjectDiscovery.FindWindowProjectAsync(directory, requested, "dev", probe ?? ReadWindowFlag, CancellationToken.None).GetAwaiter().GetResult();

    [Test, DisplayName("a solution's projects are read from .slnx and .sln")]
    public void SolutionProjectsAreRead()
    {
        foreach (string solution in new[] { "Acme.slnx", "Acme.sln" })
        {
            using TestWorkspace workspace = CreateSolutionRoot(solution);
            // Solution folders and projects that do not exist are left out; the .slnx's backslash path is read too.
            SequenceEqual(
                ["Acme.App", "Acme.Core", "Acme.Tests", "Acme.Admin"],
                ProjectDiscovery.ReadSolutionProjects(Path.Combine(workspace.Root, solution)).Select(Path.GetFileNameWithoutExtension).ToArray());
        }
    }

    [Test, DisplayName("dev from a solution root selects its one Window project")]
    public void SolutionRootSelectsTheWindowProject()
    {
        foreach (string solution in new[] { "Acme.slnx", "Acme.sln" })
        {
            using TestWorkspace workspace = CreateSolutionRoot(solution, "Acme.App");
            ProjectSelection selected = FindWindowProject(workspace.Root);
            Equal(Path.Combine(workspace.Root, "Acme.App", "Acme.App.csproj"), selected.Project);
            Equal(Path.Combine(workspace.Root, solution), selected.Solution);
            // --project names the solution, or the folder that holds it.
            Equal(selected, FindWindowProject(Path.GetTempPath(), Path.Combine(workspace.Root, solution)));
            Equal(selected, FindWindowProject(Path.GetTempPath(), workspace.Root));
        }
    }

    [Test, DisplayName("dev from a solution root with several Window projects lists them and says how to choose")]
    public void SolutionRootWithSeveralWindowProjectsLists()
    {
        using TestWorkspace workspace = CreateSolutionRoot("Acme.slnx", "Acme.App", "Acme.Admin");
        DevDevelopmentException error = Throws<DevDevelopmentException>(() => FindWindowProject(workspace.Root));
        Equal("RAPPDEV1002", error.Code);
        Equal(Runic.CommandLine.CommandExitCategory.Unavailable, error.Category);
        Contains(error.Message, "The solution Acme.slnx has 2 Window projects: Acme.App/Acme.App.csproj, Acme.Admin/Acme.Admin.csproj.");
        Contains(error.Message, "Choose one with --project, for example 'dotnet runic dev --project Acme.App/Acme.App.csproj'.");
    }

    [Test, DisplayName("dev from a solution root without a Window project says so")]
    public void SolutionRootWithoutWindowProjectFails()
    {
        using TestWorkspace workspace = CreateSolutionRoot("Acme.slnx");
        DevDevelopmentException none = Throws<DevDevelopmentException>(() => FindWindowProject(workspace.Root));
        Equal("RAPPDEV1002", none.Code);
        Contains(none.Message, "The solution Acme.slnx has no Window project: none of its 4 .csproj projects sets RunicApplicationFrontendWindowProject=true.");
        Contains(none.Message, "Use --project");

        // A project that MSBuild could not evaluate is named in the detail.
        DevDevelopmentException failed = Throws<DevDevelopmentException>(() => FindWindowProject(workspace.Root, probe: (project, token) =>
            project.EndsWith("Acme.Admin.csproj", StringComparison.Ordinal) ? Task.FromResult<bool?>(null) : ReadWindowFlag(project, token)));
        Contains(failed.Message, "MSBuild could not evaluate 1 of them");
        Contains(failed.LocalDetail ?? string.Empty, "MSBuild could not evaluate: Acme.Admin/Acme.Admin.csproj");
    }

    [Test, DisplayName("dev in a project folder keeps its project, and several solutions need --project")]
    public void ProjectFolderAndSeveralSolutions()
    {
        using TestWorkspace workspace = CreateSolutionRoot("Acme.slnx", "Acme.App", "Acme.Admin");
        // A folder with one .csproj is that project, as before, even beside a solution.
        string core = Path.Combine(workspace.Root, "Acme.Core");
        workspace.Write("Acme.Core/Acme.Core.slnx", "<Solution />");
        ProjectSelection project = FindWindowProject(core);
        Equal(Path.Combine(core, "Acme.Core.csproj"), project.Project);
        Equal(null, project.Solution);
        Equal(Path.Combine(core, "Acme.Core.csproj"), FindWindowProject(workspace.Root, "Acme.Core/Acme.Core.csproj").Project);

        workspace.Write("Other.sln", string.Empty);
        DevDevelopmentException several = Throws<DevDevelopmentException>(() => FindWindowProject(workspace.Root));
        Contains(several.Message, "Multiple solution files were found in the directory: Acme.slnx, Other.sln.");
        Contains(several.Message, "'dotnet runic dev --project Acme.slnx'");

        // Neither a project nor a solution.
        string empty = Path.Combine(workspace.Root, "empty");
        Directory.CreateDirectory(empty);
        Contains(Throws<DevDevelopmentException>(() => FindWindowProject(empty)).Message, "No .csproj was found");
        workspace.Write("notes.txt", string.Empty);
        Contains(Throws<DevDevelopmentException>(() => FindWindowProject(workspace.Root, "notes.txt")).Message, "a .sln or .slnx solution");
    }

    [Test, DisplayName("the Window project flag is read from -getProperty's value or JSON")]
    public void WindowProjectValueIsParsed()
    {
        True(ProjectDiscovery.ParseWindowProjectValue("true\n"), "A bare true is a Window project.");
        True(ProjectDiscovery.ParseWindowProjectValue("True"), "MSBuild's booleans ignore case.");
        False(ProjectDiscovery.ParseWindowProjectValue("\n"), "An empty value is not a Window project.");
        True(ProjectDiscovery.ParseWindowProjectValue("""{ "Properties": { "RunicApplicationFrontendWindowProject": "true" } }"""), "JSON output is read too.");
        False(ProjectDiscovery.ParseWindowProjectValue("{ not json"), "Unreadable output is not a Window project.");
    }
}
