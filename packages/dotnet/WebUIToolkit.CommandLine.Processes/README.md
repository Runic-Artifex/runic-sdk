# WebUIToolkit.CommandLine.Processes

Bounded, shell-free local process execution for command-line automation. The package
uses `ProcessStartInfo.ArgumentList`, captures stdout and stderr concurrently, keeps
draining after retention caps, and returns sanitized states instead of raw exceptions.

Every request is evaluated by an `IExecutablePolicy`. Use `LocalExecutablePolicy`
with explicit executable and working-directory roots when requests cross a trust
boundary. Process execution is a local capability and must not be exposed directly
to browser or UI input.

The runner resolves a name through the effective `PATH` and launches the exact path
shown to the policy. This reduces check/start inconsistency but is not an operating-
system sandbox: a permitted file or symlink can still be replaced between evaluation
and start. Inherited environment variables may contain credentials; higher-security
callers should supply a minimal child environment and an absolute allowlisted path.

```csharp
var policy = new LocalExecutablePolicy(
    executableRoots: new[] { applicationToolsDirectory },
    workingDirectoryRoots: new[] { applicationDataDirectory });
var runner = new ProcessRunner(policy);
var request = new ProcessRequest(
    toolPath,
    new[] { "export", "--output", outputPath },
    workingDirectory: applicationDataDirectory,
    options: new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(30)));

ProcessResult result = await runner.RunAsync(request, cancellationToken);
```

Arguments are always distinct tokens. Do not join or quote them into a shell command.
Output limits are byte limits; retained bytes are decoded only after both redirected
pipes are drained.

## Stable faults

| Code | Meaning |
| --- | --- |
| `WUTCLI6001` | Executable rejected by policy |
| `WUTCLI6002` | Working directory rejected by policy |
| `WUTCLI6003` | Invalid policy decision |
| `WUTCLI6004` | Policy evaluation failed safely |
| `WUTCLI6005` | Operating-system start failure |
| `WUTCLI6006` | Post-start lifecycle observation failure |

Use the matching `ProcessFaultCodes` constants when branching on these identities.
Custom policy messages and details are deliberately not copied into results; rejection
is normalized to a bounded library-owned message to prevent accidental data exposure.
