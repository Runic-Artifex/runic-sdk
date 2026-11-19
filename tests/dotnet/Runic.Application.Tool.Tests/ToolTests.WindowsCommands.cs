using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// #1081: dev starts npm and pnpm, which Node.js installs as batch files (npm.cmd), as the shell finds them.
internal sealed partial class ToolTests
{
    // Arguments that cmd.exe would interpret if it saw them unquoted, or that the C runtime's parser escapes.
    private static readonly string[] BatchArguments =
    [
        "run", "dev", "with space", "a&b", "x^y", "%OS%", "%%", "!OS!", "(paren)", "<in>|out", "say \"hi\"", "\"",
        @"C:\dir with space\", @"a\""b", @"back\slash", "", "--port", "5173",
    ];

    private static ExecutableSearch WindowsSearch(string path, IEnumerable<string> files, string? pathExtensions = ".COM;.EXE;.BAT;.CMD")
    {
        var existing = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return new(true, path, pathExtensions, @"C:\Windows\System32\cmd.exe", existing.Contains);
    }

    [Test, DisplayName("Windows finds npm.cmd on PATH through PATHEXT, skipping relative entries")]
    public void WindowsFindsBatchFileThroughPathExtensions()
    {
        ExecutableSearch search = WindowsSearch(@"Frontend;C:\Empty;""C:\Program Files\nodejs""",
            [@"Frontend\npm.exe", @"C:\Program Files\nodejs\npm", @"C:\Program Files\nodejs\npm.cmd", @"C:\Program Files\nodejs\npm.ps1"]);
        Equal(@"C:\Program Files\nodejs\npm.cmd", search.Find("npm"));
        Equal<string?>(null, search.Find("pnpm"));
    }

    [Test, DisplayName("Windows takes the first PATH directory, then PATHEXT's order")]
    public void WindowsSearchOrder()
    {
        ExecutableSearch search = WindowsSearch(@"C:\first\;C:\second", [@"C:\second\bun.exe", @"C:\first\bun.cmd", @"C:\first\bun.exe"],
            ".EXE;.CMD");
        Equal(@"C:\first\bun.exe", search.Find("bun"));
        // Without PATHEXT the shell's default list applies; a name with a listed extension is looked up as is.
        Equal(@"C:\second\bun.exe", WindowsSearch(@"C:\second", [@"C:\second\bun.exe"], null).Find("bun"));
        Equal(@"C:\second\bun.exe", WindowsSearch(@"C:\second", [@"C:\second\bun.exe"]).Find("bun.exe"));
        Equal<string?>(null, WindowsSearch(@"C:\second", [@"C:\second\bun.exe.EXE"]).Find("bun.exe"));
    }

    [Test, DisplayName("Unix searches PATH for the name itself")]
    public void UnixSearchesName()
    {
        var files = new HashSet<string>(StringComparer.Ordinal) { "relative/npm", "/usr/bin/npm.cmd", "/opt/node/bin/npm" };
        var search = new ExecutableSearch(false, "relative:/usr/bin:/opt/node/bin", ".CMD", "cmd.exe", files.Contains);
        Equal("/opt/node/bin/npm", search.Find("npm"));
        ProgramStart start = search.Prepare("npm", ["ci"]);
        Equal("npm", start.FileName);
        Equal<string?>(null, start.CommandLine);
    }

    [Test, DisplayName("Windows starts a batch file through cmd.exe with its command line in a variable")]
    public void WindowsStartsBatchFileThroughCommandInterpreter()
    {
        ExecutableSearch search = WindowsSearch(@"C:\Program Files\nodejs", [@"C:\Program Files\nodejs\npm.cmd"]);
        ProgramStart start = search.Prepare("npm", ["run", "dev", "--", "--host", "a&b"]);
        Equal(@"C:\Windows\System32\cmd.exe", start.FileName);
        Equal("/d /v:off /s /c \"%RUNIC_BATCH_COMMAND_LINE%\"", start.CommandLine);
        Equal("\"C:\\Program Files\\nodejs\\npm.cmd\" \"run\" \"dev\" \"--\" \"--host\" \"a&b\"", start.BatchCommandLine);
        Equal(0, start.Arguments.Count);

        ProcessStartInfo startInfo = CommandRunner.CreateStartInfo("npm", @"C:\work", ["ci"], search);
        Equal(@"C:\Windows\System32\cmd.exe", startInfo.FileName);
        Equal(0, startInfo.ArgumentList.Count);
        Equal(start.CommandLine, startInfo.Arguments);
        Equal("\"C:\\Program Files\\nodejs\\npm.cmd\" \"ci\"", startInfo.Environment[ExecutableSearch.BatchCommandLineVariable]);
        False(startInfo.UseShellExecute, "The batch file starts through the shell.");

        // A full path to a batch file starts the same way; .bat too.
        Equal(@"C:\Windows\System32\cmd.exe", search.Prepare(@"C:\tools\build.BAT", []).FileName);
    }

    [Test, DisplayName("Windows starts executables directly and keeps an unknown name")]
    public void WindowsStartsExecutablesDirectly()
    {
        ExecutableSearch search = WindowsSearch(@"C:\bun", [@"C:\bun\bun.exe"]);
        ProgramStart bun = search.Prepare("bun", ["install", "a&b"]);
        Equal(@"C:\bun\bun.exe", bun.FileName);
        SequenceEqual<string>(["install", "a&b"], bun.Arguments);
        Equal<string?>(null, bun.CommandLine);
        // Not found: Process.Start reports it, and dev says RAPPDEV1004 with the name.
        Equal("pnpm", search.Prepare("pnpm", ["install"]).FileName);
    }

    [Test, DisplayName("batch file arguments are quoted for cmd.exe and the C runtime")]
    public void BatchArgumentsAreQuoted()
    {
        Equal("\"a&b\"", ExecutableSearch.QuoteForBatch("a&b"));
        Equal("\"x^y\"", ExecutableSearch.QuoteForBatch("x^y"));
        Equal("\"%OS%\"", ExecutableSearch.QuoteForBatch("%OS%"));
        Equal("\"\"", ExecutableSearch.QuoteForBatch(""));
        Equal("\"say \"\"hi\"\"\"", ExecutableSearch.QuoteForBatch("say \"hi\""));
        Equal("\"C:\\dir with space\\\\\"", ExecutableSearch.QuoteForBatch(@"C:\dir with space\"));
        Equal("\"a\\\\\"\"b\"", ExecutableSearch.QuoteForBatch(@"a\""b"));
        Equal("\"back\\slash\"", ExecutableSearch.QuoteForBatch(@"back\slash"));
        // Every quote in the result pairs up, so cmd.exe reads each argument as quoted text from start to end.
        foreach (string argument in BatchArguments)
        {
            string quoted = ExecutableSearch.QuoteForBatch(argument);
            Equal(0, quoted.Count(static character => character == '"') % 2);
        }
        Throws<ArgumentException>(() => ExecutableSearch.QuoteForBatch("one\r\ntwo"));
        DevDevelopmentException failure = Throws<DevDevelopmentException>(() => CommandRunner.CreateStartInfo(
            "npm", @"C:\work", ["run", "a\nb"], WindowsSearch(@"C:\node", [@"C:\node\npm.cmd"])));
        Equal("RAPPDEV1004", failure.Code);
    }

    [Test, Category("WindowsCommands"), DisplayName("a batch file receives its arguments unchanged on Windows")]
    public async Task BatchFileReceivesArgumentsUnchanged()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        // The batch file's own directory has cmd.exe's metacharacters in its name too.
        string directory = Path.Combine(workspace.Root, "tools & ^ %OS% !OS! (x)");
        Directory.CreateDirectory(directory);
        string output = Path.Combine(workspace.Root, "arguments.json");
        (string executable, IReadOnlyList<string> prefix) = ProcessTreeSignal.SelfCommand();
        File.WriteAllText(Path.Combine(directory, "echo-arguments.cmd"),
            "@echo off\r\n" + string.Join(' ', [Quote(executable), .. prefix.Select(Quote), EchoArguments.Argument, Quote(output)]) + " %*\r\n");
        var search = new ExecutableSearch(true, directory + ";C:\\Windows\\System32", ".EXE;.CMD",
            ExecutableSearch.System.CommandInterpreter, File.Exists);

        CommandResult result = await RunAsync(CommandRunner.CreateStartInfo("echo-arguments", workspace.Root, BatchArguments, search));
        Equal(0, result.ExitCode);
        SequenceEqual<string>(BatchArguments, JsonSerializer.Deserialize<string[]>(File.ReadAllText(output))!);

        // npm.cmd hands its arguments to node.exe, whose parser must read them the same way.
        if (ExecutableSearch.System.Find("node") is { } node && !ExecutableSearch.IsBatchFile(node))
        {
            File.Delete(output);
            File.WriteAllText(Path.Combine(directory, "node-arguments.cmd"),
                $"@{Quote(node)} -e \"require('fs').writeFileSync(process.argv[1], JSON.stringify(process.argv.slice(2)))\" {Quote(output)} %*\r\n");
            result = await RunAsync(CommandRunner.CreateStartInfo("node-arguments", workspace.Root, BatchArguments, search));
            Equal(0, result.ExitCode);
            SequenceEqual<string>(BatchArguments, JsonSerializer.Deserialize<string[]>(File.ReadAllText(output))!);
        }

        static string Quote(string value) => "\"" + value + "\"";
    }

    [Test, Category("WindowsCommands"), DisplayName("stopping a batch file stops the process it started on Windows")]
    public void StoppingBatchFileStopsItsChild()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        (string executable, IReadOnlyList<string> prefix) = ProcessTreeSignal.SelfCommand();
        string batch = workspace.Write("serve.cmd",
            "@echo off\r\n" + string.Join(' ', ["\"" + executable + "\"", .. prefix.Select(static value => "\"" + value + "\""),
                StopTestChild.Argument, "run", "\"" + workspace.Root + "\""]) + "\r\n");
        RunningProcess running = RunningProcess.Start("stop-test", batch, workspace.Root, []);
        int application = WaitForStopChild(workspace.Root);
        Equal(running.Id, ParentOf(application));
        True(running.StopAsync().Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(30))), "The batch file did not stop.");
        WaitForExit(application);
        running.DisposeAsync().AsTask().Wait();
    }

    // #1163: bun exits on the console's Ctrl+C and leaves Node.js (Vite, and the runic-translations serve it started)
    // running. The stop still ends that orphan: the frontend server is in a Job object, which the stop ends whether or
    // not the process dev started has exited.
    [Test, Category("WindowsCommands"), DisplayName("stopping the frontend server ends a process its exited root left running on Windows")]
    public void StopEndsProcessLeftByExitedRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestWorkspace();
        // The grandchild ignores the stop signal, as a server that missed it would.
        workspace.Write("stuck", "");
        (string executable, IReadOnlyList<string> prefix) = ProcessTreeSignal.SelfCommand();
        RunningProcess running = RunningProcess.Start("stop-test", executable, workspace.Root,
            [.. prefix, StopTestChild.Argument, StopTestChild.LeaveRole, workspace.Root], endTreeAtStop: true);
        int orphan = WaitForStopChild(workspace.Root);
        True(running.Completion.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(30))), "The stand-in package manager did not exit.");
        False(ProcessTreeSignal.HasExited(orphan), "The stand-in Node.js process exited with its parent.");
        try
        {
            True(running.StopAsync().Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(30))), "The stop did not finish.");
            WaitForExit(orphan);
        }
        finally
        {
            running.DisposeAsync().AsTask().Wait();
            if (!ProcessTreeSignal.HasExited(orphan)) Process.GetProcessById(orphan).Kill();
        }
    }

    private static int ParentOf(int pid)
    {
        using Process process = Process.Start(new ProcessStartInfo("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", $"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').ParentProcessId"])
        { RedirectStandardOutput = true })!;
        string text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return int.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<CommandResult> RunAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TestTimeouts.Scale(TimeSpan.FromSeconds(60)));
        var result = new CommandResult(process.ExitCode, await output, await error);
        True(result.ExitCode == 0, $"The batch file exited with {result.ExitCode}:\n{result.CombinedOutput}");
        return result;
    }
}

/// <summary>A child of the test runner that writes the arguments it received to a JSON file.</summary>
internal static class EchoArguments
{
    internal const string Argument = "__echo-arguments";

    internal static int Run(string output, IReadOnlyList<string> arguments)
    {
        File.WriteAllText(output, JsonSerializer.Serialize(arguments));
        return 0;
    }
}
