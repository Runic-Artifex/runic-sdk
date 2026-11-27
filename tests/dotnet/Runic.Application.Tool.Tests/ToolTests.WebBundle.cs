using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Runic.Application.Tool.Packaging;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// Split CI S3 (D082): dotnet runic bundle writes a web bundle, dotnet runic package <target> --frontend-bundle publishes
// from it without Node.js, package passes -p: properties through (D082.5), and doctor --bundle checks a bundle offline.
internal sealed partial class ToolTests
{
    /// <summary>A web bundle as the Views targets write it: an application with a page and a client library.</summary>
    private static string WriteWebBundle(TestWorkspace workspace, string folder = "bundle")
    {
        workspace.Write($"{folder}/projects/Acme.App/www/index.html", "<!doctype html>");
        workspace.Write($"{folder}/projects/Acme.App/www/assets/app.js", "export {};");
        workspace.Write($"{folder}/projects/Acme.App/clients/counter.ts", "export {};");
        workspace.Write($"{folder}/projects/Acme.Shared/clients/runic-clients.ts", "export {};");
        string bundle = Path.Combine(workspace.Root, folder);
        // sha256sum's lines, sorted by path, as WebBundle.cs of the generator writes them.
        string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        string lines = $"{Sha("export {};")}  assets/app.js\n{Sha("<!doctype html>")}  index.html\n";
        object Contracts(params string[] views) => new
        {
            unit = "u", fingerprintFormat = "runic.views.fingerprint/1", irSha256 = Sha("ir"),
            views = views.Select(view => new { id = view, fingerprint = "09A0D538" }).ToArray(),
        };
        workspace.Write($"{folder}/web-bundle.json", JsonSerializer.Serialize(new
        {
            format = "rax.web-bundle/1",
            producer = new { name = "Runic.Application.Views", version = "0.24.0" },
            createdBy = new { os = "linux-x64", dotnet = "10.0.404" },
            projects = new object[]
            {
                new
                {
                    assembly = "Acme.App", project = "src/Acme.App/Acme.App.csproj", kind = "application", configuration = "Release",
                    targetFramework = "net10.0", host = "desktop", nodeEnv = "production", www = "projects/Acme.App/www",
                    clients = "projects/Acme.App/clients", contracts = Contracts("Acme.App.CounterViewModel", "Acme.App.MainViewModel"),
                    files = new { count = 2, sha256 = Sha(lines) },
                },
                new
                {
                    assembly = "Acme.Shared", project = "src/Acme.Shared/Acme.Shared.csproj", kind = "clientLibrary", configuration = "Release",
                    targetFramework = "net10.0", clients = "projects/Acme.Shared/clients", contracts = Contracts("Acme.Shared.AboutViewModel"),
                },
            },
        }));
        return bundle;
    }

    [Test, DisplayName("the web bundle reader checks the manifest, each entry's folders and the hash of each page")]
    public void ReadsAndChecksWebBundles()
    {
        using var workspace = new TestWorkspace();
        string bundle = WriteWebBundle(workspace);
        (IReadOnlyList<WebBundleEntry> entries, IReadOnlyList<string> problems) = WebBundle.Read(bundle);
        SequenceEqual([], problems);
        Equal(2, entries.Count);
        WebBundleEntry app = entries[0];
        Equal(("Acme.App", "application", 2, 2, "desktop", "production"), (app.Assembly, app.Kind, app.Views, app.Files, app.Host, app.NodeEnv));
        Equal((long)("<!doctype html>".Length + "export {};".Length), app.Bytes);
        Equal(("clientLibrary", 1, (string?)null), (entries[1].Kind, entries[1].Views, entries[1].Sha256));

        // A truncated download: the page no longer hashes to its files.
        File.Delete(Path.Combine(bundle, "projects", "Acme.App", "www", "assets", "app.js"));
        Contains(WebBundle.Read(bundle).Problems.Single(), "has 1 files with SHA-256");
        Contains(WebBundle.Read(bundle).Problems.Single(), "RUNICBRIDGE093");
        // The generated clients are gone too.
        Directory.Delete(Path.Combine(bundle, "projects", "Acme.Shared"), recursive: true);
        Equal(2, WebBundle.Read(bundle).Problems.Count);

        // Another format, a missing manifest, a missing folder.
        workspace.Write("bundle/web-bundle.json", """{"format":"rax.web-bundle/2","projects":[]}""");
        Contains(WebBundle.Read(bundle).Problems.Single(), "has the format 'rax.web-bundle/2', not rax.web-bundle/1");
        File.Delete(Path.Combine(bundle, "web-bundle.json"));
        Contains(WebBundle.Read(bundle).Problems.Single(), "has no web-bundle.json");
        Contains(WebBundle.Read(Path.Combine(workspace.Root, "missing")).Problems.Single(), "does not exist");

        // Required members, the kind, and a path that leaves the bundle.
        workspace.Write("bundle/web-bundle.json", """
            {"format":"rax.web-bundle/1","projects":[{"assembly":"A","kind":"tool","www":"../outside"}]}
            """);
        IReadOnlyList<string> broken = WebBundle.Read(bundle).Problems;
        Contains(string.Join("\n", broken), "lacks the required member 'producer'");
        Contains(string.Join("\n", broken), "the entry of A lacks 'project', 'configuration', 'targetFramework', 'contracts'");
        Contains(string.Join("\n", broken), "the unknown kind 'tool'");
        Contains(string.Join("\n", broken), "names the folder '../outside', which is not a relative path inside the bundle");
    }

    [Test, DisplayName("bundle builds with RunicApplicationFrontendBundleDirectory into a fresh folder and summarizes the bundle")]
    public void BundlePreparesBuildsAndSummarizes()
    {
        using var workspace = new TestWorkspace();
        string output = Path.Combine(workspace.Root, "out");
        // A new folder, and an earlier bundle, whose entries bundle replaces so that none stays in the manifest.
        BundleApplication.PrepareOutput(output);
        True(Directory.Exists(output), "bundle creates its output folder.");
        string earlier = WriteWebBundle(workspace, "out");
        BundleApplication.PrepareOutput(earlier);
        SequenceEqual([], Directory.EnumerateFileSystemEntries(earlier).ToArray());
        // A folder with other files is refused rather than emptied.
        workspace.Write("out/notes.txt", "keep");
        DevUsageException inUse = Throws<DevUsageException>(() => BundleApplication.PrepareOutput(output));
        Equal(BundleApplication.OutputInUse, inUse.Code);
        Contains(inUse.Message, "(notes.txt)");
        True(File.Exists(Path.Combine(output, "notes.txt")), "bundle deletes nothing it did not write.");

        BundleOptions options = new() { Configuration = "Release", Properties = BundleApplication.ParseProperties(["RunicApplicationHost=desktop"]) };
        SequenceEqual(["build", "/repo/Acme.slnx", "--nologo", "-c", "Release", "-property:RunicApplicationFrontendBundleDirectory=/tmp/b%3Bc",
            "-property:RunicApplicationHost=desktop"], BundleApplication.CreateBuildArguments("/repo/Acme.slnx", "/tmp/b;c", options));
        Equal(BundleApplication.PropertyFormatCode,
            Throws<DevUsageException>(() => BundleApplication.ParseProperties(["RunicApplicationFrontendPrebuiltDirectory=x"])).Code);

        // A solution is built as it is, also from its folder; a folder with a project is that project.
        workspace.Write("sln/Acme.slnx", "<Solution />");
        string solution = Path.Combine(workspace.Root, "sln", "Acme.slnx");
        Equal(solution, BundleApplication.FindInput(workspace.Root, "sln/Acme.slnx"));
        Equal(solution, BundleApplication.FindInput(Path.Combine(workspace.Root, "sln"), null));
        workspace.Write("sln/App/App.csproj", "<Project />");
        Equal(Path.Combine(workspace.Root, "sln", "App", "App.csproj"), BundleApplication.FindInput(workspace.Root, "sln/App"));

        string bundle = WriteWebBundle(workspace, "summary");
        IReadOnlyList<string> summary = BundleApplication.Summary(bundle, WebBundle.Read(bundle).Entries);
        Equal($"Web bundle (rax.web-bundle/1): {bundle}", summary[0]);
        Contains(summary[1], "Acme.App (application): 2 View(s), 2 files, 25 B, SHA-256 ");
        Contains(summary[1], "; host desktop, NODE_ENV production");
        Equal("  Acme.Shared (clientLibrary): 1 View(s), no page", summary[2]);
        Contains(summary[3], $"--frontend-bundle {bundle}");
    }

    [Test, DisplayName("package takes -p: properties and --frontend-bundle into its evaluation and publish")]
    public void PackagePassesPropertiesAndTheFrontendBundle()
    {
        // -p:NAME=VALUE (and its other spellings) is a property for package and bundle; -p <path> stays the project.
        SequenceEqual(["package", "windows", "--property", "ApplicationId=org.example.App", "--property", "A=1", "--property", "B=2",
            "--property", "C=3", "-p", "App", "--", "-p:X=Y"],
            Runic.Application.Tool.Program.RewritePropertyArguments(["package", "windows", "-p:ApplicationId=org.example.App", "/p:A=1",
                "-property:B=2", "--property:C=3", "-p", "App", "--", "-p:X=Y"]));
        SequenceEqual(["bundle", "--property", "RunicApplicationHost=desktop"],
            Runic.Application.Tool.Program.RewritePropertyArguments(["bundle", "-p:RunicApplicationHost=desktop"]));
        SequenceEqual(["dev", "-p:App"], Runic.Application.Tool.Program.RewritePropertyArguments(["dev", "-p:App"]));

        var options = new PackageOptions(PackageTarget.Windows)
        {
            Properties = PackageOptions.ParseProperties(["ApplicationId=org.example.App", "RunicPackageIcon=icons/a;b.png"]),
        };
        SequenceEqual(["-property:ApplicationId=org.example.App", "-property:RunicPackageIcon=icons/a%3Bb.png"], options.PropertyArguments(null));
        SequenceEqual(["-property:ApplicationId=org.example.App", "-property:RunicPackageIcon=icons/a%3Bb.png",
            "-property:RunicApplicationFrontendPrebuiltDirectory=/b"], options.PropertyArguments("/b"));
        foreach (string reserved in new[] { "Configuration=Debug", "runtimeidentifier=win-x64", "PublishAot=false", "RunicApplicationFrontendPrebuiltDirectory=/b" })
            Equal(PackageCodes.PropertyInvalid, Throws<DevUsageException>(() => PackageOptions.ParseProperties([reserved])).Code);
        Contains(Throws<DevUsageException>(() => PackageOptions.ParseProperties(["RunicApplicationFrontendPrebuiltDirectory=/b"])).Message, "use --frontend-bundle");
        Equal(PackageCodes.PropertyInvalid, Throws<DevUsageException>(() => PackageOptions.ParseProperties(["NoValue"])).Code);

        // --frontend-bundle: checked offline before anything is published, and passed as a full path.
        using var workspace = new TestWorkspace();
        string bundle = WriteWebBundle(workspace);
        Equal(bundle, PackageApplication.ResolveFrontendBundle("bundle", workspace.Root));
        Equal(null, PackageApplication.ResolveFrontendBundle(null, workspace.Root));
        DevDevelopmentException missing = Throws<DevDevelopmentException>(() => PackageApplication.ResolveFrontendBundle("elsewhere", workspace.Root));
        Equal(PackageCodes.PropertyInvalid, missing.Code);
        Contains(missing.Message, "--frontend-bundle does not name a usable web bundle");

        // Through the CLI: a reserved -p: property is a usage error, exit 2, before any project is read.
        (int exitCode, string output) = RunCli("package", "deb", "-p:PublishAot=false", "--project", "missing.csproj", "--format", "json");
        Equal(2, exitCode);
        Contains(output, PackageCodes.PropertyInvalid);
        Contains(output, "--property cannot set PublishAot");
    }

    [Test, DisplayName("doctor --bundle checks the web bundle and its entry for the project")]
    public void DoctorChecksAWebBundle()
    {
        using var workspace = new TestWorkspace();
        string bundle = WriteWebBundle(workspace);
        DoctorCheck pass = DoctorWorkspaceChecks.CheckWebBundle(bundle, "Acme.App");
        Equal(DoctorStatus.Pass, pass.Status);
        Contains(pass.Message, "Acme.App (application, 2 View(s), 2 files), Acme.Shared (clientLibrary, 1 View(s))");
        DoctorCheck other = DoctorWorkspaceChecks.CheckWebBundle(bundle, "Acme.Admin");
        Equal(DoctorStatus.Failure, other.Status);
        Contains(other.Message, "it has no entry for Acme.Admin, only for Acme.App, Acme.Shared, so its publish would fail with RUNICBRIDGE093");
        File.Delete(Path.Combine(bundle, "projects", "Acme.App", "www", "index.html"));
        DoctorCheck truncated = DoctorWorkspaceChecks.CheckWebBundle(bundle, "Acme.App");
        Equal(DoctorStatus.Failure, truncated.Status);
        Contains(truncated.Remediation ?? string.Empty, "dotnet runic bundle");
    }
}
