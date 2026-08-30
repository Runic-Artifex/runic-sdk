# Command Line package consumer

`Invoke-PackageConsumer.ps1` packs the five Command Line packages
(`Runic.CommandLine`, `Processes`, and `Testing`) into a fresh
local feed, then restores the template consumer into a fresh package cache. It
proves managed and host-runtime NativeAOT execution for the current platform
(Linux-x64 in CI). The consumer uses the kernel's packaged method-first analyzer,
the Hosting adapter, application-owned JSON metadata, and a bounded
`ProcessRunner` child command. It also proves an application-owned `--output`
option alongside a configured `--runic-output` transport option and variadic
generated `IReadOnlyList<string>` option and trailing positional binding,
including literal application arguments after `--`, and required generated
scalar/repeated options. It also proves a handler-owned warning diagnostic is
preserved in the JSON envelope and written to human stderr by both managed and
NativeAOT executions. It also exercises a nonzero handler failure with an
application-owned human stdout report and diagnostics/fault on stderr, while
proving that the same report is absent from the JSON failure envelope.
It also proves a generated-catalog parse failure retains an explicit JSON
transport classification while redacting the unknown option value.

Per-run artifacts use a short uniquely named directory below the OS temporary
directory so the isolated package cache and NativeAOT output stay bounded.

The fresh package cache and isolated feed prove that the consumer resolves only
the packed artifacts and their declared dependencies before managed and NativeAOT
execution.
