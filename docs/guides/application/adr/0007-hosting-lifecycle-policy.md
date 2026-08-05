# ADR 0007: Hosting lifecycle and failure precedence

- Status: Accepted
- Date: 2026-07-22

## Context

Hosting adapters compose MVVM, browser, Generic Host,
and external `cs-webui` implementations. Their dependency-neutral lifecycle behavior
must be fixed before those adapters introduce framework-specific assumptions.

## Decision

- One immutable lifecycle selects exactly one launch kind and mode runner.
- Validation occurs before host startup. Startup participants run by phase and then
  registration order; only completed participants stop, in reverse completion order.
- The first terminal non-success result or failure remains primary. A teardown failure
  may replace success, but never an earlier non-zero result or failure; later failures
  remain available as ordered secondary failures.
- Competing stop requests converge on one completion. Disposal is idempotent and may
  initiate stop for an active lifecycle.
- Every bounded wait uses an injected `TimeProvider`. Aggregate startup and shutdown
  deadlines cap individual waits, including mode execution and teardown.
- Core Hosting contracts remain independent of MVVM, command-line, Generic Host, and
  the external lowercase `cs-webui` package. Adapters depend inward on these contracts.

## Consequences

The deterministic BCL-only kernel can be tested without wall-clock sleeps or external
frameworks. Later adapters must translate their native errors and cancellation signals
into these frozen precedence and shutdown rules rather than redefining them.
ADR 0011 records the concrete cs-webui desktop-host boundary.
