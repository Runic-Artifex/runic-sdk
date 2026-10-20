// Both sides of a privileged action. nixos/privileged-operations runs the caller as an ordinary user; pkexec runs
// the same executable as root for the helper side.
//
//   Runic.Privileged.Smoke run VERB [--wait-for-go] [--cancel-on LINE] [ARGUMENT...]   caller
//
// --cancel-on cancels the run when the program prints LINE; "agent-ready" instead cancels a second after the
// action starts, while the test's authentication agent holds the request open.
//   Runic.Privileged.Smoke VERB COMMAND [ARGUMENT...]                                  helper, through pkexec
//
// Helper commands: "report EXIT ARGUMENT..." prints its identity and arguments and exits with EXIT; "wait" waits
// until the caller closes standard input; "state" prints the process state after PolkitHelper reset it; "ignore"
// ignores standard input for a few seconds and then starts a child that writes to the inherited output. "wait" and
// "ignore" also log to /run/runic-privileged-smoke/log, which outlives a caller the test kills.
using System.Collections.Immutable;
using Runic.Platform;
using Runic.Platform.Linux.Polkit;
using Runic.Privileged.Smoke;

if (args.Length > 0 && args[0] == "run") return await RunCaller(args[1..]);
return Helper(args);

static int Helper(string[] args)
{
    var statusBefore = File.Exists("/proc/self/status") ? File.ReadAllLines("/proc/self/status") : [];
    var cwdBefore = Environment.CurrentDirectory;
    if (PolkitHelper.GetCallerUserId() is not { } caller)
    {
        Console.Error.WriteLine("helper: not started through pkexec as root");
        return 2;
    }
    if (PolkitHelper.Match(PrivilegedActions.All, args, out var rest) is not { } action || rest.Length == 0)
    {
        Console.Error.WriteLine("helper: rejected arguments");
        return 2;
    }
    var environment = Environment.GetEnvironmentVariables().Keys.Cast<string>().Order(StringComparer.Ordinal);
    Console.WriteLine($"helper {action.Id} caller={caller} cwd={Environment.CurrentDirectory}");
    Console.WriteLine($"helper env {string.Join(',', environment)}");
    Console.WriteLine($"helper PATH={Environment.GetEnvironmentVariable("PATH")}");
    switch (rest[0])
    {
        case "report" when rest.Length >= 2 && int.TryParse(rest[1], out var exit):
            foreach (var argument in rest[2..]) Console.WriteLine($"arg <{argument}>");
            Console.Error.WriteLine("helper stderr line");
            return exit;
        case "wait":
            var stop = PolkitHelper.WatchCancellation();
            Log("waiting");
            Console.WriteLine("helper waiting");
            stop.WaitHandle.WaitOne();
            Log("stopped by caller");
            TryWrite("helper stopped by caller");
            return 5;
        case "state":
            string Status(string[] lines, string key) => lines.FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal))?[(key.Length + 1)..].Trim() ?? "?";
            var status = File.ReadAllLines("/proc/self/status");
            Console.WriteLine($"state before cwd={cwdBefore} umask={Status(statusBefore, "Umask")} sigign={Status(statusBefore, "SigIgn")} sigblk={Status(statusBefore, "SigBlk")}");
            Console.WriteLine($"state after cwd={Environment.CurrentDirectory} umask={Status(status, "Umask")} sigign={Status(status, "SigIgn")} sigblk={Status(status, "SigBlk")}");
            var created = "/run/runic-privileged-smoke/created";
            File.Delete(created);
            File.WriteAllText(created, "x");
            if (OperatingSystem.IsLinux()) Console.WriteLine($"state created mode={Convert.ToString((int)File.GetUnixFileMode(created), 8)}");
            var limits = File.ReadAllLines("/proc/self/limits");
            foreach (var name in new[] { "Max core file size", "Max file size", "Max open files", "Max cpu time", "Max address space", "Max data size" })
                Console.WriteLine($"state limit {name}: {string.Join(' ', (limits.FirstOrDefault(l => l.StartsWith(name, StringComparison.Ordinal)) ?? "?")[name.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2))}");
            Console.WriteLine($"state cgroup={File.ReadAllText("/proc/self/cgroup").Trim()}");
            Console.WriteLine($"state nice={File.ReadAllText("/proc/self/stat").Split(')')[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[16]} oom_score_adj={File.ReadAllText("/proc/self/oom_score_adj").Trim()}");
            return 0;
        case "ignore":
            Log($"ignoring input, pid {Environment.ProcessId}");
            for (var tick = 1; tick <= 6; tick++)
            {
                Thread.Sleep(500);
                Log($"still running {tick}{(TryWrite($"helper tick {tick}") ? "" : ", standard output is closed")}");
            }
            // A child inherits standard output and the ignored SIGPIPE, so its write fails with EPIPE.
            using (var child = System.Diagnostics.Process.Start("/bin/sh", ["-c", "echo from child"]))
            {
                child.WaitForExit();
                Log($"child exited with {child.ExitCode}");
            }
            Log("finished");
            return 0;
        default:
            Console.Error.WriteLine("helper: unknown command");
            return 2;
    }
}

static void Log(string line) => File.AppendAllText("/run/runic-privileged-smoke/log", line + "\n");

static bool TryWrite(string line)
{
    try
    {
        Console.WriteLine(line);
        Console.Out.Flush();
        return true;
    }
    catch (IOException) { return false; }
}

static async Task<int> RunCaller(string[] args)
{
    var actions = PolkitPrivilegedActions.Create(PrivilegedActions.All);
    var declaration = PrivilegedActions.All.FirstOrDefault(a => a.Verb == args.ElementAtOrDefault(0));
    if (declaration is null)
    {
        Console.Error.WriteLine("caller: unknown verb");
        return 2;
    }
    var rest = args[1..].ToList();
    if (rest.Remove("--wait-for-go")) Console.ReadLine();
    string? cancelOn = null;
    if (rest.IndexOf("--cancel-on") is var at and >= 0)
    {
        cancelOn = rest[at + 1];
        rest.RemoveRange(at, 2);
    }
    using var cancellation = new CancellationTokenSource();
    if (cancelOn == "agent-ready") cancellation.CancelAfter(TimeSpan.FromSeconds(1));
    var request = new PrivilegedActionRequest(declaration, [.. rest])
    {
        Output = line =>
        {
            Console.WriteLine($"{(line.Channel == PrivilegedOutputChannel.StandardOutput ? "out" : "err")}: {line.Text}");
            if (line.Text == cancelOn) cancellation.Cancel();
        },
    };
    try
    {
        var result = await actions.RunAsync(request, cancellation.Token);
        Console.WriteLine(result switch
        {
            PlatformResult<PrivilegedActionCompletion>.Success(var completion) => $"result: completed {completion.ExitCode}",
            PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.PermissionDenied } => "result: denied",
            PlatformResult<PrivilegedActionCompletion>.Failed { Code: PlatformFailureCode.UserDismissed } => "result: dismissed",
            PlatformResult<PrivilegedActionCompletion>.Failed(var code, var diagnostic) => $"result: failed {code} {diagnostic}",
            PlatformResult<PrivilegedActionCompletion>.Unavailable(var reason) => $"result: unavailable {reason}",
            _ => $"result: {result}",
        });
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("result: cancelled");
    }
    return 0;
}
