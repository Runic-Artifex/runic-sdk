# ADR 0018: Operational observability boundary

- Status: Accepted
- Date: 2026-08-26

## Context

The v0.2 operational-foundation wave needs one owner for the eventual
OpenTelemetry and support-bundle surfaces without turning an early release
foundation into an unversioned telemetry API or a second diagnostics system.
`dotnet runic doctor` already owns local prerequisite checks, while Toolkit and
the independent products own their respective runtime boundaries. There is no
safe support-bundle format, redaction policy, or telemetry convention to ship
yet.

## Decision

`dotnet runic` owns the future, opt-in support-bundle collection command and
its local preview/removal workflow. It may coordinate collection from product
boundaries, but it must not become a telemetry backend, exporter, or remote
upload client.

Each product owns instrumentation at the boundary it implements:

| Boundary | Owner | Responsibility |
|---|---|---|
| Application lifecycle and host composition, excluding the CS-WEBUI native boundary | Runic Application | Shared trace propagation and lifecycle semantics. |
| CS-WEBUI native boundary | CS-WEBUI | Native lifecycle and private-delivery instrumentation. |
| Application Bridge | Runic Application Bridge | Bridge request, event, and dispatch semantics. |
| Svelte projection | `@runic-artifex/svelte` | Browser-side instrumentation and redaction at the Svelte boundary. |
| Vite development diagnostics | `@runic-artifex/vite-plugin-runic` | Frontend build and development diagnostics. |
| Assets, Translations, and Editor | Respective product | Domain operations and redaction before an operational handoff. |
| Command catalog and command I/O schema | Runic Command Line generator | Command-schema and machine-envelope semantics. |
| Command coordination and support-bundle orchestration | `dotnet runic` | Local diagnostics, explicit collection, preview, and selection. |
| Release/compatibility facts | Release Automation | Authoritative manifest facts that a bundle may reference. |

OpenTelemetry exporters, telemetry storage, dashboards, and transport-specific
diagnostic backends remain application or operator choices. Runic will use
standard OpenTelemetry integration points when the semantic conventions are
ready; it will not introduce a Runic exporter or hosted observability service.

No `doctor --bundle` option, bundle schema, automatic capture, upload path,
or new OpenTelemetry package dependency is introduced by this decision. Existing
stable diagnostic IDs and sanitized public faults remain the only supported
diagnostic contract in v0.2.

The W50 operations wave owns the implementation and review of:

- versioned trace and metric names, attributes, units, and cardinality limits;
- propagation through desktop, hosted-web, in-memory, and service transports;
- redaction rules and source-linked diagnostic projections;
- deterministic, unsigned support-bundle format, preview, removal, and
  omission record; and
- privacy/security review and fault-injection evidence.

## Consequences

- The operational foundation has a single command owner without making early
  diagnostic output a telemetry compatibility promise.
- Product repositories can prepare bounded, redacted inputs without duplicating
  support-bundle orchestration or release metadata.
- A later implementation must preserve direct use of standard exporters and
  must prove that collection is explicit, inspectable, deterministic for the
  same selected inputs, and free of automatic network activity.
