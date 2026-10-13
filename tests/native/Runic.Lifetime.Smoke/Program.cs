// Claims the single instance and logs, one line each, every launch and login startup result to the file in the
// first argument. The desktop VM test in nixos/application-lifetime launches it twice and through login startup.
//
//   Runic.Lifetime.Smoke LOG [--enable-login | --disable-login | --quit | ARGUMENT...]
using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;

const string ApplicationId = "dev.runic.LifetimeSmoke";
if (args.Length < 1) { Console.Error.WriteLine("Usage: Runic.Lifetime.Smoke LOG [ARGUMENT...]"); return 2; }
var log = new Lazy<StreamWriter>(() => new StreamWriter(args[0], append: true) { AutoFlush = true });
void Log(string line) { lock (log) log.Value.WriteLine(line); }
string Describe(ApplicationLaunch launch) =>
    $"[{string.Join(' ', launch.Arguments)}] in {launch.WorkingDirectory} token={launch.PlatformContext?.ActivationToken ?? "-"}" +
    $" startup={launch.PlatformContext?.StartupId ?? "-"}";

var launch = ApplicationLaunch.FromProcess(args);
var claim = await ApplicationInstance.ClaimAsync(ApplicationId, launch);
if (claim is not PlatformResult<ApplicationInstanceClaim>.Success { Value: var claimed })
{
    Log($"claim {claim}");
    return 1;
}
if (claimed is ApplicationInstanceClaim.Forwarded)
{
    // Only the running instance writes the log; this launch reports on standard output.
    Console.WriteLine($"forwarded {Describe(launch)}");
    return 0;
}

await using var instance = ((ApplicationInstanceClaim.Primary)claimed).Instance;
var startup = PortalPlatformProvider.CreateLoginStartup(ApplicationId);
var done = new TaskCompletionSource();
async Task Handle(ApplicationLaunch received)
{
    if (received.Arguments.Contains("--quit")) { done.TrySetResult(); return; }
    if (received.Arguments.Contains("--enable-login"))
        Log($"enable {Result(await startup.EnableAsync(new LoginStartupRequest([args[0], "--from-login"]) { Reason = "Smoke test" }))}");
    if (received.Arguments.Contains("--disable-login")) Log($"disable {Result(await startup.DisableAsync())}");
    Log($"enabled {(await startup.IsEnabledAsync()) switch { PlatformResult<bool>.Success { Value: var on } => on.ToString(), var other => other.ToString() }}");
}
// Launched is raised on a worker thread, one launch at a time.
instance.Launched += (_, received) =>
{
    Log($"launched {Describe(received)}");
    Handle(received).GetAwaiter().GetResult();
};
Log($"primary {Describe(launch)}");
await Handle(launch);
await done.Task;
Log("quit");
return 0;

static string Result<T>(PlatformResult<T> result) => result switch
{
    PlatformResult<T>.Success => "success",
    PlatformResult<T>.Unavailable unavailable => $"unavailable {unavailable.Reason}",
    PlatformResult<T>.Failed failed => $"failed {failed.Code}",
    _ => result.ToString()!,
};
