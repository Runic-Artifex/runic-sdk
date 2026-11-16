using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Tool;
using TUnit.Core;
using Entry = Runic.Application.Tool.ProcessTreeSignal.ProcessEntry;

namespace Runic.Application.Tool.Tests;

/// <summary>The IDE contract of <c>dotnet runic dev</c> (#959): Window process lines, Ready, the session file and diagnostics.</summary>
internal sealed partial class ToolTests
{
    /// <summary>The regex tools/dotnet-runic/README.md documents for dev's diagnostics and the build's own errors.</summary>
    private const string DocumentedDiagnosticPattern =
        @"^\s*(?:\d+>)?(?<file>\S.*?)(?:\((?<line>\d+)(?:,(?<column>\d+))?(?:,\d+,\d+)?\))?\s*:\s+(?:\S+\s+)?(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?$";

    /// <summary>VS Code 1.119's built-in <c>$msCompile</c> pattern.</summary>
    private const string MsCompilePattern =
        @"^\s*(?:\s*\d+>)?(\S.*?)(?:\((\d+|\d+,\d+|\d+,\d+,\d+,\d+)\))?\s*:\s+(?:(\S+)\s+)?((?:fatal +)?error|warning|info)\s+(\w+\d+)?\s*:\s*(.*)$";

    [Test, DisplayName("dev prints the Window process and Ready lines in their documented form")]
    public void DevPrintsContractLines()
    {
        Equal("[dev] Window started: process 4242", DevApplication.WindowStartedLine(4242));
        Equal("[dev] Window exited: process 4242", DevApplication.WindowExitedLine(4242));
        Equal("[dev] Ready", DevApplication.ReadyLine);
        True(Regex.IsMatch(DevApplication.WindowStartedLine(17), @"^\[dev\] Window started: process (\d+)$"),
            "The documented Window line pattern does not match.");
        // The two prefix lines (since 0.21) that the template's tasks.json and runic-editors' problem matcher begin on.
        Equal("[dev] Evaluating Views Window project...", PhaseTimer.StartLine(DevApplication.EvaluatingPhase));
        True(DevApplication.ReloadingLine.StartsWith("[dev] Reloading", StringComparison.Ordinal),
            "The reload line no longer starts with the documented prefix.");
    }

    [Test, DisplayName("dev failures are one line that $msCompile and the documented regex match")]
    public void DevDiagnosticsMatchOneRegex()
    {
        string project = OperatingSystem.IsWindows() ? @"C:\src\App\App.csproj" : "/src/App/App.csproj";
        string line = DevApplication.DiagnosticLine(project, "RAPPDEV1007", "The Vite development server exited unexpectedly with code 1.");
        Equal($"{project} : error RAPPDEV1007: The Vite development server exited unexpectedly with code 1.", line);
        Equal("dotnet-runic : error RAPPDEV1002: The selected project does not exist.",
            DevApplication.DiagnosticLine(null, "RAPPDEV1002", "The selected project does not exist."));
        // A message keeps to its line.
        DoesNotContain(DevApplication.DiagnosticLine(project, "RAPPDEV1006", "one\ntwo"), "\n");

        Match documented = Regex.Match(line, DocumentedDiagnosticPattern);
        True(documented.Success, "The documented pattern does not match dev's diagnostic.");
        Equal(project, documented.Groups["file"].Value);
        Equal("RAPPDEV1007", documented.Groups["code"].Value);
        Equal("error", documented.Groups["severity"].Value);
        Equal("The Vite development server exited unexpectedly with code 1.", documented.Groups["message"].Value);
        Match vsCode = Regex.Match(line, MsCompilePattern);
        True(vsCode.Success, "$msCompile does not match dev's diagnostic.");
        Equal(project, vsCode.Groups[1].Value);
        Equal("RAPPDEV1007", vsCode.Groups[5].Value);

        // The build output dev passes through: the same pattern takes its file, line and column.
        Match build = Regex.Match("/src/App/CounterViewModel.cs(18,5): error CS1002: ; expected [/src/App/App.csproj]", DocumentedDiagnosticPattern);
        True(build.Success, "The documented pattern does not match a compiler error.");
        Equal("/src/App/CounterViewModel.cs", build.Groups["file"].Value);
        Equal("18", build.Groups["line"].Value);
        Equal("5", build.Groups["column"].Value);
        Equal("CS1002", build.Groups["code"].Value);
        Equal("; expected", build.Groups["message"].Value);
        Match bridge = Regex.Match("/src/App/CounterViewModel.cs(12,17): error RUNICBRIDGE004: The command has two overloads.", DocumentedDiagnosticPattern);
        Equal("RUNICBRIDGE004", bridge.Groups["code"].Value);
        False(Regex.IsMatch(DevApplication.WindowStartedLine(1), DocumentedDiagnosticPattern), "A contract line was taken for a diagnostic.");
    }

    [Test, DisplayName("a failing dev prints its diagnostic line with the project")]
    public async Task FailingDevPrintsDiagnosticLine()
    {
        using var workspace = new TestWorkspace();
        string project = workspace.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var console = new CapturingConsole();
        int exitCode = await Runic.Application.Tool.Program.RunAsync(["dev", "--project", project], console);
        Equal(Runic.Application.Tool.Program.DevelopmentFailure, exitCode);
        Contains(console.Error + console.Output,
            $"{Path.GetFullPath(project)} : error RAPPDEV1005: The selected project must opt into the Runic Views Window model");
    }

    [Test, DisplayName("the Window program comes from RunCommand, or the assembly under the dotnet host")]
    public void WindowProgramFollowsRunCommand()
    {
        Equal(new WindowProcessLocator.WindowProgram("App", "App.dll"),
            WindowProcessLocator.CreateProgram("/src/App/bin/Debug/net10.0/App", "App"));
        Equal(new WindowProcessLocator.WindowProgram("App", "App.dll"),
            WindowProcessLocator.CreateProgram(@"C:\src\App\bin\Debug\net10.0\App.exe", "App"));
        // macOS: dotnet run starts the development bundle's executable.
        Equal(new WindowProcessLocator.WindowProgram("Counter", "App.dll"),
            WindowProcessLocator.CreateProgram("/src/App/obj/Debug/net10.0/Counter.app/Contents/MacOS/Counter", "App"));
        // UseAppHost=false: dotnet exec App.dll.
        Equal(new WindowProcessLocator.WindowProgram("App", "App.dll"), WindowProcessLocator.CreateProgram("/usr/share/dotnet/dotnet", "App"));
        Equal(new WindowProcessLocator.WindowProgram("App", "App.dll"), WindowProcessLocator.CreateProgram("", "App"));
        // Only .exe is an extension.
        Equal(new WindowProcessLocator.WindowProgram("My.App", "My.App.dll"), WindowProcessLocator.CreateProgram("/src/bin/My.App", "My.App"));
    }

    [Test, DisplayName("the Window is the shallowest process below dotnet watch that runs the project's program")]
    public void WindowProcessIsSelectedFromTheTree()
    {
        var program = new WindowProcessLocator.WindowProgram("App", "App.dll");
        Entry[] watch =
        [
            new(100, 1, "dotnet watch --no-hot-reload --project /src/App/App.csproj run --no-launch-profile"),
            new(101, 100, "/usr/share/dotnet/dotnet /usr/share/dotnet/sdk/10.0.100/DotnetTools/dotnet-watch/dotnet-watch.dll --project /src/App/App.csproj run",
                ["/usr/share/dotnet/dotnet", "/usr/share/dotnet/sdk/10.0.100/DotnetTools/dotnet-watch/dotnet-watch.dll", "--project", "/src/App/App.csproj", "run"]),
            // Build nodes and the compiler server name the project's assembly but are not the Window.
            new(102, 101, "/usr/share/dotnet/dotnet /usr/share/dotnet/sdk/10.0.100/MSBuild.dll /nodemode:1 /src/App/obj/App.dll",
                ["/usr/share/dotnet/dotnet", "/usr/share/dotnet/sdk/10.0.100/MSBuild.dll", "/nodemode:1", "/src/App/obj/App.dll"]),
            new(103, 101, "/usr/share/dotnet/dotnet run --project /src/App/App.csproj --no-build",
                ["/usr/share/dotnet/dotnet", "run", "--project", "/src/App/App.csproj", "--no-build"]),
            new(104, 103, "/src/App/bin/Debug/net10.0/App", ["/src/App/bin/Debug/net10.0/App"]),
            // The Window's own helper process with the same name is deeper.
            new(105, 104, "/src/App/bin/Debug/net10.0/App --helper", ["/src/App/bin/Debug/net10.0/App", "--helper"]),
            // Another application with the same name that dev did not start.
            new(200, 1, "/elsewhere/App", ["/elsewhere/App"]),
        ];
        Equal<int?>(104, WindowProcessLocator.Select(100, watch, program));
        Equal<int?>(null, WindowProcessLocator.Select(103, watch.Where(process => process.Id != 104 && process.Id != 105).ToArray(), program));
        Equal<int?>(null, WindowProcessLocator.Select(999, watch, program));

        // UseAppHost=false: dotnet App.dll or dotnet exec … App.dll below dotnet run.
        Entry[] framework =
        [
            new(10, 1, "dotnet run"),
            new(11, 10, "dotnet exec --runtimeconfig /src/App/bin/App.runtimeconfig.json /src/App/bin/App.dll",
                ["dotnet", "exec", "--runtimeconfig", "/src/App/bin/App.runtimeconfig.json", "/src/App/bin/App.dll"]),
        ];
        Equal<int?>(11, WindowProcessLocator.Select(10, framework, program));
        True(WindowProcessLocator.Runs(new Entry(1, 0, "dotnet /src/App/bin/App.dll", ["dotnet", "/src/App/bin/App.dll"]), program), "dotnet App.dll is the Window.");
        False(WindowProcessLocator.Runs(new Entry(1, 0, "dotnet build /src/App/App.csproj", ["dotnet", "build", "/src/App/App.csproj"]), program), "dotnet build is not the Window.");

        // ps (macOS) prints the command line unquoted: a path with spaces still names the program at its start.
        True(WindowProcessLocator.Runs(new Entry(1, 0, "/Users/me/My Apps/App/obj/Debug/net10.0/App.app/Contents/MacOS/App --flag"), program),
            "A program path with spaces was not recognized.");
        False(WindowProcessLocator.Runs(new Entry(1, 0, "/usr/bin/tool --open /Users/me/App"), program), "An argument was taken for the program.");
        // Windows lists the executable's file name.
        True(WindowProcessLocator.Runs(new Entry(1, 0, "App.exe", ["App.exe"]), program), "App.exe is the Window.");
    }

    [Test, DisplayName("dev finds a real Window process below the process it started")]
    public async Task WindowProcessIsFoundInTheRunningTree()
    {
        using var workspace = new TestWorkspace();
        (string executable, IReadOnlyList<string> prefix) = ProcessTreeSignal.SelfCommand();
        if (OperatingSystem.IsWindows() && prefix.Count != 0) return; // Windows lists file names only, so dotnet <dll> is not told apart.
        var program = WindowProcessLocator.CreateProgram(executable, typeof(ToolTests).Assembly.GetName().Name!);
        // "parent" stands in for dotnet watch and starts the "run" child, the stand-in Window.
        RunningProcess parent = StartStopChild("parent", workspace.Root);
        try
        {
            int window = WaitForStopChild(workspace.Root);
            int? found = null;
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (found is null && deadline.Elapsed < TestTimeouts.Scale(TimeSpan.FromSeconds(30)))
            {
                found = await WindowProcessLocator.FindAsync(parent.Id, program, CancellationToken.None);
                if (found is null) await Task.Delay(100);
            }
            Equal<int?>(window, found);
        }
        finally
        {
            await parent.StopAsync();
            await parent.DisposeAsync();
        }
    }

    [Test, DisplayName("the Window monitor reports starts and exits, not a list that missed the Window")]
    public async Task WindowMonitorReportsRestarts()
    {
        var time = new FakeTimeProvider();
        var events = new List<string>();
        int? window = null;
        var exited = new HashSet<int>();
        int checks = 0;
        using var stop = new CancellationTokenSource();
        Task monitor = HostProcessController.MonitorWindowAsync(
            () => 10,
            _ =>
            {
                Interlocked.Increment(ref checks);
                return Task.FromResult(window);
            },
            exited.Contains,
            pid => events.Add($"started {pid}"),
            pid => events.Add($"exited {pid}"),
            TimeSpan.FromMilliseconds(250),
            time,
            stop.Token);

        async Task Tick()
        {
            int before = Volatile.Read(ref checks);
            time.Advance(TimeSpan.FromMilliseconds(250));
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref checks) == before && deadline.Elapsed < TestTimeouts.Scale(TimeSpan.FromSeconds(10))) await Task.Delay(5);
        }

        window = 20;
        await Tick();
        SequenceEqual(["started 20"], events);
        // A snapshot that missed the running Window does not end it.
        window = null;
        await Tick();
        await Tick();
        SequenceEqual(["started 20"], events);
        // dotnet watch restarts it after a C# edit: the old one exits, a new one starts.
        exited.Add(20);
        await Tick();
        window = 21;
        await Tick();
        SequenceEqual(["started 20", "exited 20", "started 21"], events);
        // A different process replaces the Window in one step.
        window = 22;
        await Tick();
        SequenceEqual(["started 20", "exited 20", "started 21", "exited 21", "started 22"], events);
        stop.Cancel();
        await monitor.WaitAsync(TestTimeouts.Scale(TimeSpan.FromSeconds(10)));
    }

    // #1026: IDE tasks and extensions react to [dev] Ready or a Window line and then read the session file, so each line
    // follows the write it announces. Each line is checked against the file as it is when the line is printed.
    [Test, DisplayName("a reader who sees a dev contract line finds its state in the session file")]
    public void DevContractLinesFollowTheSessionFile()
    {
        using var workspace = new TestWorkspace();
        var session = new DevSession(CreateConfiguration(workspace, "vite"), new DevOptions(null, "Debug", true, true, true, false, []), [],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        session.Start();
        var seen = new List<string>();
        void Read(string line)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(session.Path));
            JsonElement root = document.RootElement;
            JsonElement process = root.GetProperty("window").GetProperty("processId");
            seen.Add($"{line} | {root.GetProperty("state").GetString()} | {(process.ValueKind == JsonValueKind.Null ? "null" : process.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        }
        var contract = new DevApplication.ContractReporter(session, Read, write => write());

        contract.WindowStarted(4242);
        contract.WindowExited(4242);
        contract.WindowStarted(4243);
        SequenceEqual([
            "[dev] Window started: process 4242 | starting | 4242",
            "[dev] Ready | ready | 4242",
            "[dev] Window exited: process 4242 | starting | null",
            "[dev] Window started: process 4243 | starting | 4243",
            "[dev] Ready | ready | 4243",
        ], seen);

        // --no-window: dev serves only the frontend, and Ready follows the ready file too.
        seen.Clear();
        var frontendOnly = new DevSession(CreateConfiguration(workspace, "vite"),
            new DevOptions(null, "Debug", true, true, true, false, []) { StartWindow = false }, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        frontendOnly.Start();
        new DevApplication.ContractReporter(frontendOnly, Read, write => write()).Ready();
        SequenceEqual(["[dev] Ready | ready | null"], seen);
    }

    [Test, DisplayName("the dev session file describes the frontend, pages, Window and stop")]
    public void DevSessionFileDescribesTheSession()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration configuration = CreateConfiguration(workspace, "vite") with
        {
            DevelopmentServerDocument = "index.html;pages/settings.html",
            FrontendGeneratedDirectory = Path.Combine(workspace.Root, "Frontend", "src", "generated"),
            BridgeSourceMapPath = Path.Combine(workspace.Root, "obj", "Debug", "net10.0", "runic-bridge", "sources.json"),
        };
        var options = new DevOptions(null, "Debug", true, true, true, false, []);
        var session = new DevSession(configuration, options,
            [new DevSession.DebugEndpoint("webview2", "cdp", "http://127.0.0.1:9222/")],
            new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero),
            // A local start time is written in UTC.
            new DateTimeOffset(2026, 11, 14, 13, 59, 58, 500, TimeSpan.FromHours(2)));
        Equal(Path.Combine(workspace.Root, "obj", "runic", "dev-session.json"), session.Path);

        session.Start();
        using (JsonDocument starting = JsonDocument.Parse(File.ReadAllBytes(session.Path)))
        {
            JsonElement root = starting.RootElement;
            Equal(DevSession.Schema, root.GetProperty("schema").GetString());
            Equal("starting", root.GetProperty("state").GetString());
            Equal(Environment.ProcessId, root.GetProperty("processId").GetInt32());
            Equal("2026-11-14T12:00:00.0000000+00:00", root.GetProperty("startedAt").GetString());
            Equal("2026-11-14T11:59:58.5000000+00:00", root.GetProperty("processStartedAt").GetString());
            // The path dev passes to the Vite development server (RUNIC_APPLICATION_DEV_SOURCE_MAP).
            Equal(configuration.BridgeSourceMapPath, root.GetProperty("sourceMap").GetString());
            Equal(0, root.GetProperty("unavailableDebugEndpoints").GetArrayLength());
            Equal(configuration.ProjectPath, root.GetProperty("project").GetString());
            Equal("vite", root.GetProperty("frontend").GetProperty("developmentServer").GetString());
            Equal(JsonValueKind.Null, root.GetProperty("frontend").GetProperty("url").ValueKind);
            Equal(JsonValueKind.Null, root.GetProperty("window").GetProperty("processId").ValueKind);
            Equal("App", root.GetProperty("window").GetProperty("program").GetString());
        }

        session.FrontendReady(new Uri("http://127.0.0.1:5173/"), confirmsSession: true);
        session.WindowStarted(4242);
        session.Ready();
        using (JsonDocument ready = JsonDocument.Parse(File.ReadAllBytes(session.Path)))
        {
            JsonElement root = ready.RootElement;
            Equal("ready", root.GetProperty("state").GetString());
            JsonElement frontend = root.GetProperty("frontend");
            Equal("http://127.0.0.1:5173/", frontend.GetProperty("url").GetString());
            Equal(5173, frontend.GetProperty("port").GetInt32());
            Equal("http://127.0.0.1:5173/__runic/state", frontend.GetProperty("stateUrl").GetString());
            Equal(configuration.FrontendGeneratedDirectory, frontend.GetProperty("generatedDirectory").GetString());
            Equal(configuration.RuntimeWebRoot, root.GetProperty("webRoot").GetString());
            JsonElement[] pages = [.. root.GetProperty("pages").EnumerateArray()];
            Equal(2, pages.Length);
            Equal("pages/settings.html", pages[1].GetProperty("document").GetString());
            Equal("http://127.0.0.1:5173/pages/settings.html", pages[1].GetProperty("url").GetString());
            Equal(Path.Combine(configuration.RuntimeWebRoot, "pages", "settings.html"), pages[1].GetProperty("file").GetString());
            Equal(4242, root.GetProperty("window").GetProperty("processId").GetInt32());
            Equal(1, root.GetProperty("window").GetProperty("starts").GetInt32());
            Equal("http://127.0.0.1:9222/", root.GetProperty("debugEndpoints")[0].GetProperty("url").GetString());
        }

        // A restart: not usable until the next Window starts.
        session.WindowExited(4242);
        using (JsonDocument restarting = JsonDocument.Parse(File.ReadAllBytes(session.Path)))
        {
            Equal("starting", restarting.RootElement.GetProperty("state").GetString());
            Equal(JsonValueKind.Null, restarting.RootElement.GetProperty("window").GetProperty("processId").ValueKind);
        }

        session.WindowStarted(4243);
        session.Stop();
        using (JsonDocument stopped = JsonDocument.Parse(File.ReadAllBytes(session.Path)))
        {
            Equal("stopped", stopped.RootElement.GetProperty("state").GetString());
            Equal(JsonValueKind.Null, stopped.RootElement.GetProperty("window").GetProperty("processId").ValueKind);
            Equal(2, stopped.RootElement.GetProperty("window").GetProperty("starts").GetInt32());
        }
        // Each write replaces the file whole; no temporary file stays behind.
        SequenceEqual(["dev-session.json"], Directory.GetFiles(Path.GetDirectoryName(session.Path)!).Select(Path.GetFileName).ToArray());

        // Without a development server there is no URL; the page is the built index.html.
        DevProjectConfiguration built = CreateConfiguration(workspace, string.Empty);
        var builtSession = new DevSession(built with { BridgeSourceMapPath = configuration.BridgeSourceMapPath }, options, [],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        using JsonDocument document = JsonDocument.Parse(builtSession.Render());
        // No development server, so dev passes the source map to nothing.
        Equal(JsonValueKind.Null, document.RootElement.GetProperty("sourceMap").ValueKind);
        Equal(JsonValueKind.Null, document.RootElement.GetProperty("frontend").GetProperty("developmentServer").ValueKind);
        Equal(JsonValueKind.Null, document.RootElement.GetProperty("pages")[0].GetProperty("url").ValueKind);
        Equal(0, document.RootElement.GetProperty("debugEndpoints").GetArrayLength());
    }

    // #1044: the unpacked Runic DevTools extension ships beside the tool, and the session file names it for a browser's
    // --load-extension and a WebView2 Window's extension loading.
    [Test, DisplayName("the tool ships the DevTools extension and the session file names its directory")]
    public void DevSessionNamesTheDevToolsExtension()
    {
        string extension = DevToolsExtension.Find() ?? throw new InvalidOperationException("The tool has no DevTools extension beside it.");
        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(extension, "manifest.json"))))
        {
            Equal(3, manifest.RootElement.GetProperty("manifest_version").GetInt32());
            string page = manifest.RootElement.GetProperty("devtools_page").GetString()!;
            True(File.Exists(Path.Combine(extension, page)), page);
            // The public key fixes the id: Chromium's is the key's SHA-256, its first 32 hex digits as a-p.
            byte[] key = Convert.FromBase64String(manifest.RootElement.GetProperty("key").GetString()!);
            string id = string.Concat(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(key))[..32].Select(static digit => (char)('a' + Convert.ToInt32(digit.ToString(), 16))));
            Equal(DevToolsExtension.Id, id);
            // The build stamps the package version: X.Y.Z, or X.Y.Z.N for a prerelease X.Y.Z-label.N.
            string package = typeof(DevToolsExtension).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
            Equal(package, manifest.RootElement.GetProperty("version_name").GetString());
            Match prerelease = Regex.Match(package, @"^(\d+\.\d+\.\d+)(?:-(?:.*?\.)?(\d+)$|-.*$|$)");
            string expected = prerelease.Groups[1].Value + (package.Contains('-') ? "." + (prerelease.Groups[2].Success ? prerelease.Groups[2].Value : "0") : "");
            Equal(expected, manifest.RootElement.GetProperty("version").GetString());
        }
        // The documented id.
        True(File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "debugging.md")).Contains(DevToolsExtension.Id, StringComparison.Ordinal),
            "docs/debugging.md does not name the extension's id.");
        True(File.Exists(Path.Combine(extension, "panel.html")) && File.Exists(Path.Combine(extension, "sidebar.html")), "The extension's pages are missing.");
        StartsWith(DevToolsExtension.Hint(extension), $"[dev] DevTools extension: {extension} ");
        Equal(null, DevToolsExtension.Find(Path.GetTempPath()));

        using var workspace = new TestWorkspace();
        var options = new DevOptions(null, "Debug", true, true, true, false, []);
        var session = new DevSession(CreateConfiguration(workspace, "vite"), options, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, extension);
        using (JsonDocument document = JsonDocument.Parse(session.Render()))
        {
            Equal(extension, document.RootElement.GetProperty("devtoolsExtension").GetString());
            Equal(DevToolsExtension.Id, document.RootElement.GetProperty("devtoolsExtensionId").GetString());
        }
        var without = new DevSession(CreateConfiguration(workspace, "vite"), options, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        using JsonDocument none = JsonDocument.Parse(without.Render());
        Equal(JsonValueKind.Null, none.RootElement.GetProperty("devtoolsExtension").ValueKind);
        Equal(JsonValueKind.Null, none.RootElement.GetProperty("devtoolsExtensionId").ValueKind);
    }

    [Test, DisplayName("the session lists WebView2's debugging port when the Window's environment enables it")]
    public void DevSessionFindsWebView2DebuggingPort()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration desktop = CreateConfiguration(workspace, "vite") with { ApplicationHosts = ["desktop"] };
        const string variable = DevSession.WebView2ArgumentsVariable;
        KeyValuePair<string, string>[] port9222 = [new(variable, "--remote-debugging-port=9222 --enable-features=X")];
        Equal("http://127.0.0.1:9222/", DevSession.FindDebugEndpoints(desktop, port9222, _ => null, windows: true, elevated: false).Single().Url);
        // dev's own environment, when -e does not set it; -e wins.
        Equal("http://127.0.0.1:9333/", DevSession.FindDebugEndpoints(desktop, [], name => name == variable ? "--remote-debugging-port=9333" : null, windows: true, elevated: false).Single().Url);
        Equal("http://127.0.0.1:9222/", DevSession.FindDebugEndpoints(desktop, port9222, _ => "--remote-debugging-port=9333", windows: true, elevated: false).Single().Url);
        // Only WebView2 reads the variable: not on Linux or macOS, and not for a CS-WebUI-only application.
        Equal(0, DevSession.FindDebugEndpoints(desktop, port9222, _ => null, windows: false, elevated: false).Count);
        Equal(0, DevSession.FindDebugEndpoints(desktop with { ApplicationHosts = ["cswebui"] }, port9222, _ => null, windows: true, elevated: false).Count);
        Equal(0, DevSession.FindDebugEndpoints(desktop, [new(variable, "--remote-debugging-port=0")], _ => null, windows: true, elevated: false).Count);
        Equal(0, DevSession.FindDebugEndpoints(desktop, [new(variable, "--remote-debugging-pipe")], _ => null, windows: true, elevated: false).Count);
    }

    // #996: WebView2 (Runtime 150 and later) ignores WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS in an elevated process; #993
    // measured it on a GitHub Windows runner, whose steps run elevated.
    [Test, DisplayName("an elevated Window's WebView2 endpoint is unavailable, with a hint, not in debugEndpoints")]
    public void DevSessionLeavesOutTheElevatedWindowsEndpoint()
    {
        using var workspace = new TestWorkspace();
        DevProjectConfiguration desktop = CreateConfiguration(workspace, "vite") with { ApplicationHosts = ["desktop"] };
        KeyValuePair<string, string>[] port9222 = [new(DevSession.WebView2ArgumentsVariable, "--remote-debugging-port=9222")];
        DevSession.DebugEndpoint endpoint = DevSession.FindDebugEndpoints(desktop, port9222, _ => null, windows: true, elevated: true).Single();
        Equal(new DevSession.DebugEndpoint("webview2", "cdp", "http://127.0.0.1:9222/", "elevated"), endpoint);
        // Not on Linux or macOS, where no endpoint is listed in the first place.
        Equal(0, DevSession.FindDebugEndpoints(desktop, port9222, _ => null, windows: false, elevated: true).Count);

        var session = new DevSession(desktop, new DevOptions(null, "Debug", true, true, true, false, []), [endpoint],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        using (JsonDocument document = JsonDocument.Parse(session.Render()))
        {
            Equal(0, document.RootElement.GetProperty("debugEndpoints").GetArrayLength());
            JsonElement unavailable = document.RootElement.GetProperty("unavailableDebugEndpoints").EnumerateArray().Single();
            SequenceEqual(["engine", "protocol", "url", "reason"], unavailable.EnumerateObject().Select(property => property.Name).ToArray());
            Equal("http://127.0.0.1:9222/", unavailable.GetProperty("url").GetString());
            Equal("elevated", unavailable.GetProperty("reason").GetString());
        }
        SequenceEqual([endpoint], session.UnavailableDebugEndpoints.ToArray());

        string hint = DevApplication.UnavailableEndpointHint(endpoint);
        True(hint.StartsWith("[dev] Debugging: WebView2 ignores WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS in an elevated process", StringComparison.Ordinal), hint);
        True(hint.Contains("http://127.0.0.1:9222/", StringComparison.Ordinal), hint);
        True(hint.EndsWith("tools/dotnet-runic/README.md#session-file", StringComparison.Ordinal), hint);
        True(DevApplication.SessionFileHelpUri.StartsWith(RunicReleaseLinks.SourceUrl, StringComparison.Ordinal), DevApplication.SessionFileHelpUri);
        // The hint's anchor is the README's section.
        True(File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "dotnet-runic", "README.md")).Contains("\n### Session file\n", StringComparison.Ordinal),
            "the README has the Session file heading");
    }

    // #1001: readers match processId with the dev process's start time, so a reused process ID does not read as running.
    [Test, DisplayName("a created session records dev's own process start time")]
    public void DevSessionRecordsTheProcessStartTime()
    {
        using var workspace = new TestWorkspace();
        DevSession session = DevSession.Create(CreateConfiguration(workspace, "vite"), new DevOptions(null, "Debug", true, true, true, false, []));
        using JsonDocument document = JsonDocument.Parse(session.Render());
        DateTimeOffset written = DateTimeOffset.Parse(document.RootElement.GetProperty("processStartedAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Equal(TimeSpan.Zero, written.Offset);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        True((written - new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero)).Duration() < TimeSpan.FromSeconds(1),
            $"processStartedAt {written:O} is the process start time {process.StartTime.ToUniversalTime():O}");
        True(written <= DateTimeOffset.UtcNow, "the process started before the session");
    }
}
