# Dependency Notices threat model v1

Manual configuration, metadata, paths, evidence bytes, SPDX expressions, and output names are attacker-controlled. Wave A makes no network APIs available to the core or engine and admits network only through an explicit `Acquire` policy decision; the acquisition implementation itself is out of scope.

## Enforced boundaries

- Config is capped at 1 MiB, evidence at 16 MiB per asset, and JSON depth at 32.
- Duplicate JSON properties are rejected before model projection.
- Evidence paths must be relative, contain no dot traversal or alternate-data-stream separator, remain beneath the declared root after full normalization, and must not escape through an existing symbolic link or reparse point.
- Evidence is hashed as raw bytes with SHA-256 before any decoding. Hashes establish integrity, not authenticity.
- `Scan`, `Evaluate`, `Generate`, and `Verify` cannot be opted into network access. `Acquire` requires a separate explicit flag and has no transport implementation in Wave A.
- Deterministic output identities exclude host paths, clocks, environment values, user caches, and unordered collections.

Archive processing, HTTP redirect/host/size controls, NuGet/npm inspection, render-time HTML escaping, atomic output replacement, and cache concurrency remain required gates for later owning phases; Wave A does not claim those implementations.
