# Command Line kernel sample

This package-free `net10.0` sample composes the Wave B kernel directly through
project references. It demonstrates the intended separation of concerns:

1. `CreateCatalog` explicitly registers a neutral command, its grammar, a
   closed options binder, a closed handler factory, and a result codec.
2. `PortableCommandSyntaxAdapter` deterministically parses the already-tokenized
   arguments without reflection or process-global lookups.
3. `CommandExecutor` owns the invocation scope and executes the typed
   `ProbeHandler`.
4. `CommandOutputDispatcher` writes either human text or exactly one JSON
   envelope using `SampleJsonContext` source-generated metadata.
5. `ProcessRunner` invokes a shell-free self-probe with an exact-executable
   policy, a five-second timeout, bounded output, cancellation, and a bounded
   drain period.

Run from the repository root:

```powershell
dotnet restore samples/CommandLine.Kernel/CommandLine.Kernel.csproj --locked-mode
dotnet run --project samples/CommandLine.Kernel/CommandLine.Kernel.csproj -- probe --label demo
dotnet run --project samples/CommandLine.Kernel/CommandLine.Kernel.csproj -- probe --label demo --output=json
$env:WEBUITOOLKIT_CLI_OUTPUT = 'json'
dotnet run --project samples/CommandLine.Kernel/CommandLine.Kernel.csproj -- probe
```

The machine protocol remains `webuitoolkit.cli/1`. Output selection remains
`--output` first, then `WEBUITOOLKIT_CLI_OUTPUT`, then the human default.

No external parser package is adopted: the sample uses the deterministic
library-owned parser control while an external package pin remains unapproved.
The project references are for repository verification; package consumers use
the same public surface from the built packages.

Hosting integration and UI-launch classification are intentionally deferred to
Wave C. This sample does not reference or emulate the Hosting adapter.
