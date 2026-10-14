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
// until the caller closes standard input.
using System.Collections.Immutable;
using Runic.Platform;
using Runic.Platform.Linux.Polkit;
using Runic.Privileged.Smoke;

if (args.Length > 0 && args[0] == "run") return await RunCaller(args[1..]);
return Helper(args);

static int Helper(string[] args)
{
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
            var stop = PolkitHelper.WatchStandardInput();
            Console.WriteLine("helper waiting");
            stop.WaitHandle.WaitOne();
            Console.WriteLine("helper stopped by caller");
            return 5;
        default:
            Console.Error.WriteLine("helper: unknown command");
            return 2;
    }
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
