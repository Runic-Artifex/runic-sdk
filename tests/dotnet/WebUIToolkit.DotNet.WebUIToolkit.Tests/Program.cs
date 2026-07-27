using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using WebUIToolkit.DotNet.WebUIToolkit;

namespace WebUIToolkit.DotNet.WebUIToolkit.Tests;

internal static class Program
{
    public static int Main()
    {
        (string Name, Action Body)[] tests =
        [
            ("dev options preserve application arguments", DevOptionsPreserveApplicationArguments),
            ("application help remains an application argument", ApplicationHelpRemainsApplicationArgument),
            ("dev options reject unknown switches", DevOptionsRejectUnknownSwitches),
            ("project discovery accepts a directory", ProjectDiscoveryAcceptsDirectory),
            ("project discovery rejects ambiguity", ProjectDiscoveryRejectsAmbiguity),
            ("commands keep arguments shell-free", CommandsKeepArgumentsShellFree),
            ("Vite server arguments are explicit and loopback-only", ViteArgumentsAreExplicit),
            ("Vite bridge forwards cwhtml diagnostics through the native overlay", ViteBridgeForwardsDiagnostics),
            ("asset mirroring updates its owned graph", AssetMirroringUpdatesOwnedGraph),
        ];

        int failures = 0;
        foreach ((string name, Action body) in tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} development-tool tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void DevOptionsPreserveApplicationArguments()
    {
        DevOptions options = DevOptions.Parse(
            ["dev", "--project", "App.csproj", "--no-restore", "--", "--advanced", "two words"]);
        Equal("App.csproj", options.Project);
        False(options.Restore, "The no-restore option was ignored.");
        if (!options.WatchHost)
        {
            throw new InvalidOperationException("The managed host watcher was disabled by default.");
        }
        SequenceEqual(["--advanced", "two words"], options.ApplicationArguments);

        DevOptions once = DevOptions.Parse(["dev", "--no-dotnet-watch"]);
        if (once.WatchHost)
        {
            throw new InvalidOperationException("--no-dotnet-watch was ignored.");
        }
    }

    private static void DevOptionsRejectUnknownSwitches()
    {
        Throws<DevUsageException>(() => DevOptions.Parse(["dev", "--wat"]));
    }

    private static void ApplicationHelpRemainsApplicationArgument()
    {
        False(
            DevOptions.RequestsHelp(["dev", "--", "--help"]),
            "Application help was consumed by the development tool.");
        DevOptions options = DevOptions.Parse(["dev", "--", "--help"]);
        SequenceEqual(["--help"], options.ApplicationArguments);
    }

    private static void ProjectDiscoveryAcceptsDirectory()
    {
        using var workspace = new TestWorkspace();
        string expected = workspace.Write("Application.csproj", "<Project />");
        Equal(expected, ProjectDiscovery.Find(workspace.Root, workspace.Root));
    }

    private static void ProjectDiscoveryRejectsAmbiguity()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("One.csproj", "<Project />");
        workspace.Write("Two.csproj", "<Project />");
        Throws<DevUsageException>(() => ProjectDiscovery.Find(workspace.Root, null));
    }

    private static void CommandsKeepArgumentsShellFree()
    {
        ProcessStartInfo startInfo = CommandRunner.CreateStartInfo(
            "dotnet",
            Environment.CurrentDirectory,
            ["build", "a project.csproj", "-p:Value=$(not-a-shell)"]);
        Equal(3, startInfo.ArgumentList.Count);
        Equal("a project.csproj", startInfo.ArgumentList[1]);
        Equal("-p:Value=$(not-a-shell)", startInfo.ArgumentList[2]);
        False(startInfo.UseShellExecute, "Commands unexpectedly use a shell.");
    }

    private static void ViteArgumentsAreExplicit()
    {
        var configuration = new DevProjectConfiguration(
            ProjectPath: "/repo/App.csproj",
            ProjectDirectory: "/repo",
            WorkspaceRoot: "/repo",
            Workspace: "@example/app",
            FrontendPackageDirectory: "/repo/frontend",
            FrontendOutputDirectory: "/repo/frontend/dist",
            FrontendWebRoot: "www",
            ContractSource: "",
            ContractCSharpOutput: "",
            ContractTypeScriptOutput: "",
            ContractTool: "",
            FrontendWatchTarget: "WebUIToolkitFrontendWatchAssets",
            ViteDevServerEnabled: true,
            ViteDevServerEntry: "/src/main.js",
            ViteConfigurationPath: "/repo/frontend/vite.config.mjs",
            CwhtmlDiagnosticsPath: "/repo/obj/Debug/net10.0/cwhtml/diagnostics.json",
            TargetDirectory: "/repo/bin/Debug/net10.0");
        IReadOnlyList<string> arguments = ViteDevelopmentServer.CreateArguments(
            configuration,
            43123,
            "/tmp/webuitoolkit/vite.config.mjs");
        SequenceEqual(
            [
                "run",
                "dev",
                "--workspace",
                "@example/app",
                "--",
                "--config",
                "/tmp/webuitoolkit/vite.config.mjs",
                "--host",
                "127.0.0.1",
                "--port",
                "43123",
                "--strictPort",
            ],
            arguments);
    }

    private static void ViteBridgeForwardsDiagnostics()
    {
        var configuration = new DevProjectConfiguration(
            ProjectPath: "/repo/App.csproj",
            ProjectDirectory: "/repo",
            WorkspaceRoot: "/repo",
            Workspace: "@example/app",
            FrontendPackageDirectory: "/repo/frontend",
            FrontendOutputDirectory: "/repo/frontend/dist",
            FrontendWebRoot: "www",
            ContractSource: "",
            ContractCSharpOutput: "",
            ContractTypeScriptOutput: "",
            ContractTool: "",
            FrontendWatchTarget: "WebUIToolkitFrontendWatchAssets",
            ViteDevServerEnabled: true,
            ViteDevServerEntry: "/src/main.js",
            ViteConfigurationPath: "/repo/frontend/vite.config.mjs",
            CwhtmlDiagnosticsPath: "/repo/obj/Debug/net10.0/cwhtml/diagnostics.json",
            TargetDirectory: "/repo/bin/Debug/net10.0");
        string source = ViteConfigurationBridge.CreateSource(configuration);
        Contains(source, "webuitoolkit.cwhtml.diagnostics/1.0");
        Contains(source, "webuitoolkit:cwhtml-diagnostics");
        Contains(source, "server.ws.send({ type: \"error\"");
        Contains(source, "document.querySelector(\"vite-error-overlay\")?.remove()");
        Contains(source, "/repo/obj/Debug/net10.0/cwhtml/diagnostics.json");
        Contains(source, "/repo/frontend/src/main.js");
    }

    private static void AssetMirroringUpdatesOwnedGraph()
    {
        using var workspace = new TestWorkspace();
        string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "destination")).FullName;
        Write(Path.Combine(source, "assets", "app.js"), "one");
        Write(Path.Combine(source, "simple", "index.html"), "simple");
        Write(Path.Combine(destination, "vendor", "bootstrap.css"), "vendor");
        var mirror = new AssetMirror(source, destination);
        Equal(2, mirror.Synchronize());

        Write(Path.Combine(source, "assets", "app.js"), "two");
        File.Delete(Path.Combine(source, "simple", "index.html"));
        Write(Path.Combine(source, "advanced", "index.html"), "advanced");
        Equal(3, mirror.Synchronize());

        Equal("two", File.ReadAllText(Path.Combine(destination, "assets", "app.js")));
        False(
            File.Exists(Path.Combine(destination, "simple", "index.html")),
            "A stale owned asset was retained.");
        Equal("advanced", File.ReadAllText(Path.Combine(destination, "advanced", "index.html")));
        Equal("vendor", File.ReadAllText(Path.Combine(destination, "vendor", "bootstrap.css")));
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Equal(expected[index]!, actual[index]!);
        }
    }

    private static void False(bool value, string message)
    {
        if (value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Contains(string value, string expected)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected text containing '{expected}'.");
        }
    }

    private static void Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class TestWorkspace : IDisposable
    {
        internal TestWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "webuitoolkit-dev-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string Write(string relativePath, string content)
        {
            string path = Path.Combine(Root, relativePath);
            Program.Write(path, content);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
