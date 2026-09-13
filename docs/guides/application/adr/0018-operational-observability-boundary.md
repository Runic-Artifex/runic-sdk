# ADR 0018: Operational observability boundary

- Status: Accepted
- Date: 2026-08-26

## Context

This decision assigns diagnostic responsibilities to existing product boundaries.
`dotnet runic doctor` owns local prerequisite checks. A support-bundle format and
shared telemetry conventions are not supported public contracts.

## Decision

`dotnet runic` is the designated owner for opt-in support-bundle collection and
local preview/removal. This is an ownership decision, not an available command.
It may coordinate collection from product
boundaries, but it must not become a telemetry backend, exporter, or remote
upload client.

Each product owns instrumentation at the boundary it implements:

| Boundary                                                                           | Owner                              | Responsibility                                                     |
| ---------------------------------------------------------------------------------- | ---------------------------------- | ------------------------------------------------------------------ |
| Application lifecycle and host composition, excluding the CS-WEBUI native boundary | Runic Application                  | Shared trace propagation and lifecycle semantics.                  |
| CS-WEBUI native boundary                                                           | CS-WEBUI                           | Native lifecycle and private-delivery instrumentation.             |
| Application Bridge                                                                 | Runic Application Bridge           | Bridge request, event, and dispatch semantics.                     |
| Svelte projection                                                                  | `@runic-artifex/svelte`            | Browser-side instrumentation and redaction at the Svelte boundary. |
| Vite development diagnostics                                                       | `@runic-artifex/vite-plugin-runic` | Frontend build and development diagnostics.                        |
| Assets, Translations, and Editor                                                   | Respective product                 | Domain operations and redaction before an operational handoff.     |
| Command catalog and command I/O schema                                             | Runic Command Line generator       | Command-schema and machine-envelope semantics.                     |
| Command coordination and support-bundle orchestration                              | `dotnet runic`                     | Local diagnostics, explicit collection, preview, and selection.    |
| Release/compatibility facts                                                        | Release Automation                 | Authoritative manifest facts that a bundle may reference.          |

OpenTelemetry exporters, telemetry storage, dashboards, and transport-specific
diagnostic backends remain application or operator choices. Instrumentation uses
standard OpenTelemetry integration points; a Runic exporter or hosted observability
service is outside this boundary.

No `doctor --bundle` option, bundle schema, automatic capture, upload path,
or new OpenTelemetry package dependency is introduced by this decision. Existing
stable diagnostic IDs and sanitized public faults remain the only supported
diagnostic contract in v0.2.

## Consequences

- The operational foundation has a single command owner without making early
  diagnostic output a telemetry compatibility promise.
- Product repositories can prepare bounded, redacted inputs without duplicating
  support-bundle orchestration or release metadata.
- A later implementation must preserve direct use of standard exporters and
  must prove that collection is explicit, inspectable, deterministic for the
  same selected inputs, and free of automatic network activity.
