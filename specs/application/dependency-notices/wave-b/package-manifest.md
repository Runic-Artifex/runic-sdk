# Wave B package and dependency manifest

All projects target `net10.0`, use the `WebUIToolkit.DependencyNotices.*` identity family, and keep committed `packages.lock.json` files portable. A committed lock MUST contain only the portable `net10.0` target and MUST NOT contain a runtime-identifier section.

| Implementation identity | Responsibility | Allowed owned dependencies | Forbidden dependencies |
|---|---|---|---|
| `WebUIToolkit.DependencyNotices.Core` | Immutable models, PURL, SPDX, evidence digests, policy, diagnostics, canonical comparers | BCL | HTTP, NuGet client/protocol, npm, MSBuild, UI |
| `WebUIToolkit.DependencyNotices.Engine` | Manual inventory, safe paths, offline orchestration and verification primitives | Core, BCL | Network transport, MSBuild, UI |
| `WebUIToolkit.DependencyNotices.Policy` | Versioned policy parsing, override validation, and complete deterministic evaluation | Core, BCL | Inventory adapters, HTTP, rendering, MSBuild, UI |
| `WebUIToolkit.DependencyNotices.NuGet` | Read already-restored NuGet lock/assets/package metadata | Core, BCL | Restore, feeds, credentials, NuGet protocol network clients |
| `WebUIToolkit.DependencyNotices.Npm` | Read already-restored npm lock graph, manifests, and local evidence | Core and, only for shared safe-path primitives, Engine; BCL | Executing npm/node, registry clients, lifecycle scripts |
| `WebUIToolkit.DependencyNotices.Sbom` | Bounded CycloneDX/SPDX subset readers and reconciliation | Core, BCL | Full SBOM generation, registry/network clients |
| `WebUIToolkit.DependencyNotices.Acquisition` | Explicit online preparation and content-addressed evidence store | Core/Engine, BCL HTTP | Build invocation, implicit credentials, registry search |
| `WebUIToolkit.DependencyNotices.Rendering` | Canonical JSON, text, HTML, and manifest rendering | Core, BCL | Rescanning, policy mutation, HTTP, UI framework |
| `WebUIToolkit.DependencyNotices.Tool` | Reference CLI and compile-time adapter/renderer composition | Engine plus explicit owned adapters/renderers/acquisition | Assembly scanning, plugin discovery, MSBuild APIs |
| `WebUIToolkit.DependencyNotices.Runtime` | Read-only v2 document loader/query API and generated JSON metadata | BCL | Engine, scanners, policy evaluator, HTTP, dynamic code |
| `WebUIToolkit.DependencyNotices.Build` | Thin asset-only `buildTransitive` package that invokes an explicitly supplied packaged tool path | Tool process contract | Task assemblies, restore/acquisition, or reimplementation of scan/policy/render logic |

Rows describe the allowed architecture even when a delivery project is deferred. They do not authorize creation outside registered Dependency Notices ownership.

## Dependency direction

```text
Runtime ------------------------------> BCL
Core ---------------------------------> BCL
Engine -------------------------------> Core
Policy -------------------------------> Core
NuGet / Npm / Sbom -------------------> Core (and documented Engine primitives only)
Acquisition --------------------------> Core / Engine
Rendering ----------------------------> Core
Tool ---------------------------------> Engine + explicit adapters + Rendering + Acquisition
Build --------------------------------> packaged Tool process contract (explicit executable path)
```

The runtime model is intentionally separate from the generator model. Runtime consumers MUST NOT pull scanners, transports, package metadata readers, or the policy engine into an application.

## Lock and restore contract

- Normal verification uses `dotnet restore <project> --locked-mode` against each committed portable lock.
- A RID/AOT restore MUST use an ignored intermediate lock, for example:

  ```powershell
  dotnet publish <project> -c Release -r <rid> --self-contained true `
    -p:PublishAot=true `
    -p:NuGetLockFilePath=obj/aot.packages.lock.json `
    -p:RestoreLockedMode=false
  ```

- The intermediate AOT lock and `obj/` output MUST NOT be committed.
- Project references are permitted for implementation builds. G2 distribution evidence MUST additionally pack local packages, restore clean consumers exclusively from that feed, and execute the produced artifacts so project-reference-only behavior cannot pass unnoticed.

## Publication state

Package IDs, descriptions, authorship, symbols, signing, provenance, and repository metadata remain subject to the license publication hold. The package manifest is an architecture contract, not a publication approval.
