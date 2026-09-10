# Command Line package consumer

Run `pwsh -NoProfile -File Invoke-PackageConsumer.ps1 -PackageVersion <version>
-PackageDirectory <candidate-nuget-directory>` (optionally `-RuntimeIdentifier <rid>`).
The script consumes the existing candidate feed and restores the template consumer
into a fresh package cache. It proves managed and host-runtime NativeAOT execution
on the selected platform. It does not rebuild or repack the candidates.

The consumer uses the optional Spectre package and CommandApp with a built-in string codec,
as well as the kernel's packaged method-first analyzer,
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

The same run copies the maintained `hello-world` and command-tree tutorial C#
sources outside the checkout and builds them with PackageReferences. It executes
the greeting and hosted service, help and empty-input UI-selection paths. This
checks that the tutorials need no repository-only generator/build imports.
The application UI branch is a console fixture; it does not open a desktop window.
