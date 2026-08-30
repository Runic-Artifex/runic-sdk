# Runic.CommandLine.Processes

`Runic.CommandLine.Processes` runs local tools for command applications without
building a shell command string. It preserves argument tokens, applies an
executable policy before launch, bounds retained stdout/stderr, and returns a
sanitized result for success, rejection, timeout, cancellation, and start
failure.

## Install

```bash
dotnet add package Runic.CommandLine.Processes --prerelease
```

The package targets `net10.0` and brings `Runic.CommandLine` transitively. It is
a preview package. Use it for trusted application-controlled
automation, not as a browser-facing execution endpoint or operating-system
sandbox.

## Run an allowlisted tool

Constrain executable and working-directory roots, pass one argument per token,
and inspect the resulting state:

```csharp
var policy = new LocalExecutablePolicy(
    executableRoots: new[] { applicationToolsDirectory },
    workingDirectoryRoots: new[] { applicationDataDirectory });
var runner = new ProcessRunner(policy);
var request = new ProcessRequest(
    toolPath,
    new[] { "export", "--output", outputPath },
    workingDirectory: applicationDataDirectory,
    options: new ProcessExecutionOptions(
        timeout: TimeSpan.FromSeconds(30),
        standardOutputLimitBytes: 64 * 1024,
        standardErrorLimitBytes: 64 * 1024));

ProcessResult result = await runner.RunAsync(request, cancellationToken);

if (result.State == ProcessState.Exited && result.ExitCode == 0)
{
    return result.StandardOutput.Text;
}

throw new InvalidOperationException(
    $"Tool ended in {result.State} ({result.Fault?.Code ?? "no fault code"}).");
```

`ProcessStartInfo.ArgumentList` receives each argument separately—never join or
quote them into a shell command. Both redirected streams are drained
concurrently even after their retention caps; `IsTruncated` and
`ObservedByteCount` distinguish retained text from the total observed output.

## Security and failure behavior

Every request is evaluated by `IExecutablePolicy`. `LocalExecutablePolicy`
checks configured roots and the runner launches the path it evaluated, but this
does not eliminate operating-system races: a permitted file or symlink can be
replaced between evaluation and start. When inputs cross a trust boundary, use
an absolute allowlisted executable and a minimal child environment—inheritance
can expose credentials.

Use `ProcessState` to handle terminal behavior and `ProcessFaultCodes` for
stable diagnostics such as `RCLI6001` (executable rejected), `RCLI6002`
(working directory rejected), and `RCLI6005` (start failure). Policy messages
and details are normalized before they reach a result to avoid leaking input.

## Documentation and support

Read the [Runic Command Line documentation](https://docs.runic-artifex.eu/products/runic-command-line/),
see [process examples](https://github.com/Runic-Artifex/runic-command-line/tree/main/tests/Runic.CommandLine.Processes.Tests),
or [report an issue](https://github.com/Runic-Artifex/runic-command-line/issues).
Runic.CommandLine.Processes is maintained by Runic Artifex and licensed under the
[MIT License](https://github.com/Runic-Artifex/runic-command-line/blob/main/LICENSE).
