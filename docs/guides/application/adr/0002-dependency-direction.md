# ADR 0002: Dependency direction and integration boundaries

- Status: Accepted
- Date: 2026-07-22

## Decision

Dependencies point from composition and adapters toward neutral contracts and kernels. Feature packages never reference the application host.

The initial rules are:

1. `WebUIToolkit.MVVM` owns protocol/session/runtime contracts and has no dependency on Hosting, Flow, or a frontend framework.
2. `WebUIToolkit.MVVM.Flow` depends on minimal MVVM abstractions plus Microsoft.Extensions.DependencyInjection.Abstractions, Logging.Abstractions, and Options; it never depends on Hosting.
3. `WebUIToolkit.Hosting` composes MVVM, command-line, assets, and upstream `cs-webui` through explicit adapters.
4. `WebUIToolkit.CommandLine`, `WebUIToolkit.TextResources`, `WebUIToolkit.Collections`, and `WebUIToolkit.DependencyNotices` remain independently usable.
5. Observable collection bridge code belongs to MVVM or frontend adapters, not the BCL-only collection package.
6. Text Resources owns text/template/asset metadata; Hosting.Build may aggregate it but does not redefine it.
7. There is one HTMX implementation package: `WebUIToolkit.MVVM.Html.Htmx`.

Cross-domain integration occurs through versioned packages, schemas, manifests, conformance corpora, and approved public APIs. Direct project references across worktrees are not accepted as a handoff.
