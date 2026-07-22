# Policy fixture corpus

These schema-version-1 inputs exercise the strict policy parser and adversarial override behavior. Evaluation fixtures use the fixed date `2026-07-22`; a consumer must never substitute the machine clock.

The corpus deliberately includes duplicate JSON properties, unknown properties, escaped lone-surrogate property names and values, non-canonical Package URLs, conflicting exact overrides, expired overrides, and version-stale overrides. Override fixtures contain synthetic digests and reviewer identities only. Decisions represent organizational policy classifications and are not legal conclusions.

`fixture-manifest.json` is ordered by fixture identifier. `parseValid` distinguishes contract validity from evaluation success; a valid policy may still produce the listed deterministic diagnostic.
