using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Runic.Platform;
using Runic.Platform.Windows.Elevation;
using TUnit.Core;
using static Runic.Elevation.Smoke.Checks;

namespace Runic.Elevation.Smoke;

/// <summary>
/// Windows runs. <c>NativeElevation</c> starts this executable as a stand-in helper with CreateProcess instead of
/// runas, through the real pipe, so the DACL, the client and server checks, the helper's process state and its
/// output redirection run without a UAC prompt. <c>RealElevation</c> starts the NativeAOT smoke helper installed
/// under Program Files (<c>--test-parameter helper-directory=...</c>) with ShellExecuteExW and runas; it needs a
/// process that elevates without a prompt, as an administrator's elevated process does.
/// </summary>
internal sealed partial class NativeTests
{
    private static readonly string Self = Environment.ProcessPath!;

    [Test, Explicit, Category("NativeElevation"), DisplayName("stand-in: arguments, both channels, tools' inherited output and the exit code over the real pipe")]
    public async Task StandInRunsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lines = new ConcurrentQueue<PrivilegedActionOutput>();
        string[] arguments = [.. ProtocolTests.AwkwardArguments.Where(a => !a.Contains('\n', StringComparison.Ordinal)).Take(8)];
        var result = await StandIn("any").RunAsync(new(PrivilegedActions.Report, [.. arguments]) { Output = lines.Enqueue });
        CheckReport(result, lines, arguments, Path.GetFileName(Self));
    }

    [Test, Explicit, Category("NativeElevation"), DisplayName("stand-in: the helper replaces the environment, working directory and TEMP")]
    public async Task StandInEnvironmentAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable(SmokeActions.Canary, "from the caller");
        var lines = new ConcurrentQueue<PrivilegedActionOutput>();
        var result = await StandIn("any").RunAsync(new(PrivilegedActions.Environment, []) { Output = lines.Enqueue });
        CheckEnvironment(result, lines);
    }

    [Test, Explicit, Category("NativeElevation"), DisplayName("stand-in: cancellation stops a waiting action, and terminates one that ignores it")]
    public async Task StandInCancellationAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        await CheckCancellationAsync(StandIn("any"), StandIn("any", new WindowsElevationOptions { TerminateAfterCancellation = TimeSpan.FromSeconds(1) }));
    }

    [Test, Explicit, Category("NativeElevation"), DisplayName("stand-in: a client that connects first is disconnected, and the helper still runs")]
    public async Task StandInImpostorAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = new StandInHost("any") { Impostor = true };
        var result = await new ElevatedActionRunner(PrivilegedActions.All, PrivilegedActions.WindowsElevation, AppContext.BaseDirectory, host)
            .RunAsync(new(PrivilegedActions.Flood, ["3"]));
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success({ ExitCode: 0 }), $"result {result}");
        Check(host.ImpostorConnected && host.ImpostorDisconnected, $"impostor connected {host.ImpostorConnected}, disconnected {host.ImpostorDisconnected}");
    }

    [Test, Explicit, Category("NativeElevation"), DisplayName("stand-in: a helper started by another process is refused by both sides")]
    public async Task StandInWrongParentAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        // cmd.exe starts the helper, so the helper's parent is not the pipe's server, and the client is not the
        // process the run started.
        var host = new StandInHost("any") { ThroughCmd = true };
        var result = await new ElevatedActionRunner(PrivilegedActions.All, PrivilegedActions.WindowsElevation, AppContext.BaseDirectory, host)
            .RunAsync(new(PrivilegedActions.Wait, [])).AsTask().WaitScaledAsync(TimeSpan.FromSeconds(60));
        Check(result is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.Other, { Code: 7 }), $"result {result}");
    }

    [Test, Explicit, Category("NativeElevation"), DisplayName("stand-in: an unelevated helper reports UAC off, and a JIT helper refuses")]
    public async Task StandInRefusalsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var elevated = ElevationProbe.IsElevated();
        var result = await StandIn("elevated").RunAsync(new(PrivilegedActions.Flood, ["1"]));
        Console.WriteLine($"stand-in with the elevation check, test process elevated={elevated}: {result}");
        Check(elevated
            ? result is PlatformResult<PrivilegedActionCompletion>.Success({ ExitCode: 0 })
            : result is PlatformResult<PrivilegedActionCompletion>.Unavailable(PlatformUnavailableReason.BackendUnavailable), $"elevated={elevated}: {result}");
        var jit = RuntimeFeature.IsDynamicCodeSupported;
        result = await StandIn("nativeaot").RunAsync(new(PrivilegedActions.Flood, ["1"]));
        Console.WriteLine($"stand-in with the NativeAOT check, JIT={jit}: {result}");
        if (jit) Check(result is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, { Code: 4 }), $"JIT: {result}");
    }

    [Test, Explicit, Category("RealElevation"), DisplayName("runas: the installed NativeAOT helper runs elevated with its arguments and output")]
    public async Task RealRunsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lines = new ConcurrentQueue<PrivilegedActionOutput>();
        string[] arguments = [.. ProtocolTests.AwkwardArguments.Where(a => !a.Contains('\n', StringComparison.Ordinal)).Take(8)];
        var result = await Real().RunAsync(new(PrivilegedActions.Report, [.. arguments]) { Output = lines.Enqueue });
        CheckReport(result, lines, arguments, Path.GetFileName(Self));
    }

    [Test, Explicit, Category("RealElevation"), DisplayName("runas: the elevated helper is NativeAOT and replaces the environment it got")]
    public async Task RealEnvironmentAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable(SmokeActions.Canary, "from the caller");
        var lines = new ConcurrentQueue<PrivilegedActionOutput>();
        var result = await Real().RunAsync(new(PrivilegedActions.Environment, []) { Output = lines.Enqueue });
        CheckEnvironment(result, lines);
        Check(lines.Any(l => l.Text == "nativeaot=True"), "the installed helper is NativeAOT");
    }

    [Test, Explicit, Category("RealElevation"), DisplayName("runas: cancellation stops a waiting action, and terminates one that ignores it through the handle")]
    public async Task RealCancellationAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        await CheckCancellationAsync(Real(), Real(new WindowsElevationOptions { TerminateAfterCancellation = TimeSpan.FromSeconds(1) }));
    }

    [Test, Explicit, Category("RealElevation"), DisplayName("runas: many lines through a slow callback arrive in order")]
    public async Task RealFloodAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lines = new List<string>();
        var result = await Real().RunAsync(new(PrivilegedActions.Flood, ["20000"])
        {
            Output = line =>
            {
                if (lines.Count % 2000 == 0) Thread.Sleep(50);
                lines.Add(line.Text);
            },
        });
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success({ ExitCode: 0 }), $"result {result}");
        Check(lines.Count == 20000 && lines.Select((l, i) => l == $"line {i}").All(x => x), $"{lines.Count} lines in order");
    }

    [Test, Explicit, Category("RealElevation"), DisplayName("runas: a helper users can change, or a missing one, is never started")]
    public async Task RealUntrustedAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "runic-elevation-untrusted-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(Path.Combine(HelperDirectory(), "Runic.Elevation.Smoke.exe"), Path.Combine(directory, "Runic.Elevation.Smoke.exe"));
            var result = await Real(new WindowsElevationOptions { ApplicationDirectory = directory }).RunAsync(new(PrivilegedActions.Flood, ["1"]));
            Check(result is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, { Code: 3 }), $"the user's temporary directory: {result}");
            var missing = await Real(new WindowsElevationOptions { ApplicationDirectory = Path.Combine(directory, "missing") }).RunAsync(new(PrivilegedActions.Flood, ["1"]));
            Check(missing is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, { Code: 1 }), $"missing: {missing}");
        }
        finally { Directory.Delete(directory, recursive: true); }
        Check(WindowsElevationHost.Instance.CheckProgram(Path.Combine(HelperDirectory(), "Runic.Elevation.Smoke.exe")) == ProgramCheck.Trusted, "the installed helper is trusted");
    }

    [Test, Explicit, Category("RealElevation"), DisplayName("runas: a JIT build and a single-file JIT build of the helper under Program Files are refused before the prompt")]
    public async Task RealJitHelperAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var parameter in new[] { "jit-helper-directory", "singlefile-helper-directory" })
        {
            var directory = TestContext.Parameters.TryGetValue(parameter, out var values) && values.LastOrDefault() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"Pass --test-parameter {parameter}=<an installed JIT build of the smoke helper>.");
            Check(!File.Exists(Path.Combine(directory, "Runic.Elevation.Smoke.dll")) || parameter == "jit-helper-directory", $"{parameter} has only the executable");
            var result = await Real(new WindowsElevationOptions { ApplicationDirectory = directory }).RunAsync(new(PrivilegedActions.Flood, ["1"]));
            Check(result is PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, { Code: 4 }), $"{parameter}: {result}");
        }
        Check(HelperImage.Check(Path.Combine(HelperDirectory(), "Runic.Elevation.Smoke.exe")) == ProgramCheck.Trusted, "the NativeAOT helper passes");
    }

    [Test, Explicit, Category("RealElevation"), DisplayName("runas: the installed helper's manifest requires administrator")]
    public void RealManifest()
    {
        if (!OperatingSystem.IsWindows()) return;
        var manifest = ElevationProbe.ReadManifest(Path.Combine(HelperDirectory(), "Runic.Elevation.Smoke.exe"));
        Console.WriteLine(manifest);
        Check(manifest?.Contains("level=\"requireAdministrator\"", StringComparison.Ordinal) == true, $"manifest: {manifest}");
    }

    private static void CheckReport(PlatformResult<PrivilegedActionCompletion> result, ConcurrentQueue<PrivilegedActionOutput> lines, string[] arguments, string caller)
    {
        Console.WriteLine(string.Join("\n", lines.Select(l => $"{l.Channel}: {(l.Text.Length > 80 ? l.Text[..80] + "…" : l.Text)}")));
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success({ ExitCode: 3 }), $"result {result}");
        var stdout = lines.Where(l => l.Channel == PrivilegedOutputChannel.StandardOutput).Select(l => l.Text).ToList();
        var stderr = lines.Where(l => l.Channel == PrivilegedOutputChannel.StandardError).Select(l => l.Text).ToList();
        Check(stdout.Take(arguments.Length + 1).SequenceEqual(["verb=report", .. arguments.Select(a => $"arg <{a}>")]), $"arguments: {string.Join(" | ", stdout)}");
        Check(stdout.Contains($"caller={caller}") && stdout.Contains($"caller-pid={Environment.ProcessId}"), "the caller");
        Check(stderr.Contains("from stderr"), "stderr");
        Check(stdout.Any(l => l.Trim() == "from-child") && stderr.Any(l => l.Trim() == "child-error"), "a tool the helper started writes to the inherited output");
        Check(string.Concat(stdout.Where(l => l.Length > 0 && l.All(c => c == 'x'))) == new string('x', 40000), "a long line in parts");
        Check(stdout[^1] == "no newline at the end", "the last line without a newline, flushed at exit");
    }

    [SupportedOSPlatform("windows")]
    private static void CheckEnvironment(PlatformResult<PrivilegedActionCompletion> result, ConcurrentQueue<PrivilegedActionOutput> lines)
    {
        Console.WriteLine(string.Join("\n", lines.Select(l => l.Text)));
        Check(result is PlatformResult<PrivilegedActionCompletion>.Success({ ExitCode: 0 }), $"result {result}");
        string Value(string key) => lines.Select(l => l.Text).FirstOrDefault(t => t.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..] ?? "";
        Check(string.Equals(Value("cwd").TrimEnd('\\'), Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase), $"cwd {Value("cwd")}");
        Check(Value("canary") == "<none>", "the caller's variable is gone");
        Check(Value("path").StartsWith(Environment.SystemDirectory + ";", StringComparison.OrdinalIgnoreCase) && !Value("path").Contains("dotnet", StringComparison.OrdinalIgnoreCase), $"path {Value("path")}");
        var temp = Value("temp");
        Check(Path.GetFileName(temp).StartsWith("runic-elevated-", StringComparison.Ordinal)
            && string.Equals(Path.GetDirectoryName(temp), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), StringComparison.OrdinalIgnoreCase), $"temp {temp}");
        Check(!Directory.Exists(temp) || !ElevationProbe.IsElevated(), "the private TEMP is removed at exit");
        var variables = Value("variables").Split(',');
        Check(!variables.Any(v => v.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || v.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) || v is "USERPROFILE" or "APPDATA"), $"variables {Value("variables")}");
    }

    private static async Task CheckCancellationAsync(IDesktopPrivilegedActions waiting, IDesktopPrivilegedActions terminating)
    {
        using (var cancellation = new CancellationTokenSource())
        {
            var lines = new ConcurrentQueue<string>();
            var result = await waiting.RunAsync(new(PrivilegedActions.Wait, [])
            {
                Output = line =>
                {
                    lines.Enqueue(line.Text);
                    if (line.Text == "waiting") cancellation.Cancel();
                },
            }, cancellation.Token).AsTask().WaitScaledAsync(TimeSpan.FromSeconds(60));
            Check(result is PlatformResult<PrivilegedActionCompletion>.Success({ ExitCode: 5 }), $"wait: {result}");
            Check(lines.SequenceEqual(["waiting", "stopped by caller"]), string.Join(" | ", lines));
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await terminating.RunAsync(new(PrivilegedActions.Ignore, []) { Output = line => { if (line.Text == "ignoring") cancellation.Cancel(); } }, cancellation.Token)
                .AsTask().WaitScaledAsync(TimeSpan.FromSeconds(60));
            Check(result is PlatformResult<PrivilegedActionCompletion>.Success({ ExitCode: WindowsPrivilegedActions.TerminatedExitCode }), $"ignore: {result}");
            Check(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"terminated after {stopwatch.Elapsed}");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            await cancellation.CancelAsync();
            var thrown = false;
            try { await waiting.RunAsync(new(PrivilegedActions.Wait, []), cancellation.Token); }
            catch (OperationCanceledException) { thrown = true; }
            Check(thrown, "cancelled before it starts");
        }
    }

    private static string HelperDirectory() =>
        TestContext.Parameters.TryGetValue("helper-directory", out var values) && values.LastOrDefault() is { Length: > 0 } directory
            ? directory
            : throw new InvalidOperationException("Pass --test-parameter helper-directory=<the installed smoke helper's directory>.");

    [SupportedOSPlatform("windows")]
    private static IDesktopPrivilegedActions Real(WindowsElevationOptions? options = null)
    {
        // A prompt nobody answers would hang the run: only an elevated process elevates without one.
        if (!ElevationProbe.IsElevated())
            throw new InvalidOperationException("The RealElevation cases need an elevated test process, which elevates without a UAC prompt.");
        return WindowsPrivilegedActions.Create(PrivilegedActions.All, (options ?? new()) with { ApplicationDirectory = options?.ApplicationDirectory ?? HelperDirectory() });
    }

    [SupportedOSPlatform("windows")]
    private static ElevatedActionRunner StandIn(string mode, WindowsElevationOptions? options = null) =>
        new(PrivilegedActions.All, options ?? PrivilegedActions.WindowsElevation, AppContext.BaseDirectory, new StandInHost(mode));

    /// <summary>The real pipe; CreateProcess of this executable instead of runas; no trust check.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class StandInHost(string mode) : IElevationHost
    {
        public bool Impostor { get; init; }
        public bool ThroughCmd { get; init; }
        public bool ImpostorConnected { get; private set; }
        public bool ImpostorDisconnected { get; private set; }
        private string? _pipeName;

        public ProgramCheck CheckProgram(string path) => ProgramCheck.Trusted;

        public IElevationPipe CreatePipe(string name)
        {
            _pipeName = name;
            return WindowsElevationHost.Instance.CreatePipe(name);
        }

        public Task<LaunchResult> LaunchAsync(string program, string commandLine, nint ownerWindow)
        {
            if (Impostor)
            {
                // Connects before the helper starts; the run must drop it and wait for the helper.
                var impostor = new NamedPipeClientStream(".", _pipeName!, PipeDirection.InOut);
                impostor.Connect(5000);
                ImpostorConnected = true;
                _ = Task.Run(() =>
                {
                    var buffer = new byte[1];
                    try { ImpostorDisconnected = impostor.Read(buffer) == 0; }
                    catch (IOException) { ImpostorDisconnected = true; }
                    impostor.Dispose();
                });
            }
            var start = ThroughCmd
                ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c \"\"{Self}\" --stand-in {mode} {commandLine}\"")
                : new ProcessStartInfo(Self, $"--stand-in {mode} {commandLine}");
            start.UseShellExecute = false;
            var process = Process.Start(start)!;
            return Task.FromResult(new LaunchResult(new StartedProcess(process), 0));
        }
    }

    private sealed class StartedProcess(Process process) : IHelperProcess
    {
        public int Id => process.Id;
        public Task<int> Exited { get; } = process.WaitForExitAsync().ContinueWith(_ => process.ExitCode, TaskScheduler.Default);

        public bool TryTerminate(int exitCode)
        {
            if (!OperatingSystem.IsWindows() || process.HasExited) return false;
            return TerminateProcess(process.SafeHandle, (uint)exitCode);
        }

        public void Dispose() => process.Dispose();
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class ElevationProbe
{
    public static bool IsElevated() => ElevationNative.IsElevated() == true;

    /// <summary>The executable's embedded manifest (resource 1 of type RT_MANIFEST).</summary>
    public static string? ReadManifest(string path)
    {
        var module = LoadLibraryExW(path, 0, 0x2 /* LOAD_LIBRARY_AS_DATAFILE */);
        if (module == 0) return null;
        try
        {
            var resource = FindResourceW(module, 1, 24);
            if (resource == 0) return null;
            var data = LoadResource(module, resource);
            var size = SizeofResource(module, resource);
            return data == 0 ? null : Encoding.UTF8.GetString((byte*)LockResource(data), (int)size);
        }
        finally { FreeLibrary(module); }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryExW(string path, nint file, uint flags);

    [LibraryImport("kernel32.dll")]
    private static partial nint FindResourceW(nint module, nint name, nint type);

    [LibraryImport("kernel32.dll")]
    private static partial nint LoadResource(nint module, nint resource);

    [LibraryImport("kernel32.dll")]
    private static partial nint LockResource(nint data);

    [LibraryImport("kernel32.dll")]
    private static partial uint SizeofResource(nint module, nint resource);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint module);
}
