# Executable verification fixtures

These projects are test inputs and acceptance harnesses, not usage samples.
They exercise package-consumer boundaries, deterministic lifecycle behavior,
protocol edge cases, isolated feeds, and release-rehearsal scenarios.

Most fixtures are intentionally excluded from the main solution because their
own verification scripts control restore sources, package caches, runtime
identifiers, or execution order:

- `CommandLine.Kernel` validates the complete typed command pipeline.
- `Hosting.*` validate lifecycle and composition contracts.
- `Htmx` validates mutation, validation, cancellation, stale requests, and OOB
  rendering in one vertical slice.
- `ReferenceApplication` is the Wave G package-only release consumer.

Approachable, project-reference examples live under [`../../samples`](../../samples).
