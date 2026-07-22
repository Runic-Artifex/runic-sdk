# CLI, runtime, and Native-AOT contract

## Reference CLI

The CLI is a frontend over in-process operations; it contains no second policy or inventory implementation. Paths are resolved relative to an explicit working/input root, never a user profile inferred by the tool.

| Command | Network | Mutating | Contract |
|---|---|---|---|
| `scan` | Never | Only with `--write-lock` | Read selected restored inventories/manual config; print or explicitly replace evidence lock |
| `policy` | Never | No | Evaluate observed inventory/evidence against an explicit policy/profile |
| `generate` | Never | Declared outputs only | Build v2 model and atomically render selected formats |
| `verify` | Never | No | Recompute model/output digests; report drift |
| `sbom --verify` | Never | No | Read bounded SBOM subset and reconcile |
| `acquire --allow-network` | Explicit only | Evidence store and reviewed origin/lock update | Fetch one pinned origin under acquisition policy |

`generate`, `verify`, policy evaluation, SBOM reconciliation, and build integration cannot delegate to `acquire`. A missing asset is an error, not a reason to fetch.

### Exit codes

| Code | Meaning |
|---:|---|
| 0 | Success; no blocking diagnostic |
| 1 | Unexpected/internal failure after sanitization |
| 2 | Invalid command line, unsupported schema, or invalid configuration |
| 3 | Inventory or required evidence incomplete/invalid |
| 4 | Policy rejected or requires an unsatisfied selection/obligation |
| 5 | Generated-output drift |
| 6 | SBOM reconciliation mismatch |
| 7 | Acquisition policy, network, timeout, size, or digest failure |

This mapping is frozen: implementations MUST NOT renumber categories to match another WebUIToolkit CLI. When multiple categories occur, the command returns the most operation-specific nonzero code: acquisition 7, SBOM 6, drift 5, policy 4, inventory/evidence 3, command/configuration 2, then unexpected 1. Cancellation is reported deterministically according to the invoking surface and MUST NOT be presented as success.

Human diagnostics go to stderr by default. `--diagnostics-format json` emits the versioned diagnostics document with stable code, original severity, PURL, sanitized source, optional zero-based offset, message, and remediation. Normal machine output goes to stdout. Prompts, progress spinners, color, and terminal width MUST NOT affect redirected output.

## In-process operations

Reusable operations accept immutable request records, cancellation, streams/readers where applicable, an explicit file abstraction/root, and a diagnostic sink. They return results and diagnostics; they do not call `Environment.Exit`, mutate process-global culture, change the current directory, inspect consoles, or use static mutable registries.

Built-in adapters and renderers are registered through closed compile-time factories. Assembly scanning, arbitrary type activation, dynamic loading, and reflection-based JSON contracts are outside the AOT executable.

## Runtime-neutral surface

`WebUIToolkit.DependencyNotices.Runtime` is a read-only consumer of frozen Wave A document v1 and complete document v2. It:

- loads from caller-owned `Stream`, `ReadOnlyMemory<byte>`, or explicit path;
- uses generated `JsonSerializerContext` metadata and bounded parsing;
- exposes immutable enumeration and ordinal PURL lookup/search;
- preserves diagnostic messages as data, projects the documented v1 defaults without rewriting the input, and rejects versions outside the supported v1-v2 range;
- performs no inventory, acquisition, policy evaluation, rendering, environment discovery, or logging; and
- has no dependency on Engine, Tool, ecosystem adapters, MSBuild, or a UI framework.

The embedded-resource scenario is owned by the consuming app/build package: runtime accepts the stream and does not use reflection to discover resources.

## Native AOT and trimming

G2 AOT evidence includes the actual published tool and a clean packed-package Runtime consumer. Both are published with trimming/AOT warnings treated as errors. The consumer loads embedded v2 JSON, enumerates and looks up notices, validates expected data, and exits zero without dynamic-code requirements.

RID publishing uses the ignored lock path described in `package-manifest.md`. After AOT verification, ordinary portable `--locked-mode` restore is rerun and the worktree must be clean. Passing `dotnet build` is not a substitute for executing the native binary.

Globalization, path casing, current culture, current directory, single-file extraction, and an empty user cache are varied in tests. Output hashes must match the managed reference execution when the output contract is the same.

## Build surface boundary

The asset-only `WebUIToolkit.DependencyNotices.Build` package is thin: its `buildTransitive` props/targets translate declared MSBuild properties into the packaged CLI process contract through an explicit `DependencyNoticesToolPath`. It contains no task assembly, restore, acquisition, or scanner implementation. Root build/CI integration and shared repository properties remain deferred until the publication hold and integration gate are resolved.
