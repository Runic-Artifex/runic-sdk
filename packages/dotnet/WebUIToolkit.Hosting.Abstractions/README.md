# WebUIToolkit.Hosting.Abstractions

Wave A freezes the dependency-neutral vocabulary consumed by the lifecycle kernel.
The assembly targets `net10.0`, references only the BCL, and does not reference MVVM,
command-line, `cs-webui`, dependency injection, or a concrete Generic Host package.

## Diagnostic allocation

The Hosting family owns `WUTHOST0001`–`WUTHOST9999`. Wave A allocates the following
exact identities; the shared registry update remains an orchestrator handoff:

| Identity | Meaning |
|---|---|
| `WUTHOST0001`–`WUTHOST0007` | Reserved for the generator diagnostics renamed from the application-host plan |
| `WUTHOST1001` | Validation or invalid-launch failure |
| `WUTHOST1101` | Host start failure |
| `WUTHOST1102` | Participant start failure |
| `WUTHOST1103` | Startup timeout |
| `WUTHOST1201` | Mode-runner selection failure |
| `WUTHOST1202` | Mode-runner execution failure |
| `WUTHOST1301` | External cancellation |
| `WUTHOST1401` | Participant stop failure |
| `WUTHOST1402` | Teardown operation timeout |
| `WUTHOST1403` | Host stop failure |
| `WUTHOST1404` | Host disposal failure |
| `WUTHOST1405` | Total shutdown timeout |

All messages attached to these identities are safe summaries. Exceptions are retained
for in-process diagnostics but exception messages are not promoted to stable output.

## H0 decisions for orchestrator review

- One immutable lifecycle selects exactly one `LaunchKind` and mode runner.
- Host, participant, mode, and stop seams remain neutral; later Generic Host, MVVM,
  command-line, and external `cs-webui` adapters depend on these contracts.
- Startup is phase-then-registration order. Only completed participants stop, in
  reverse completion order.
- The first terminal result or failure remains primary. A later teardown failure can
  replace success, but it cannot replace a non-zero result or earlier failure.
- Every bounded wait uses an injected `TimeProvider`; timeout advances cleanup.
