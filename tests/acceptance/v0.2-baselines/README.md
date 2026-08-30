# v0.2 archived baseline harness

The harness preserves one fail-closed historical receipt contract for the v0.2
package-consumer path. It is not a current local package-consumption test.
It always archives the exact examples revision `494b7325d08ba405713f6ea0fe26680772caa3f1`
and editor revision `5bcb157004deaf196a3dc8e6c7d911d7c6f881d7`; it never measures
the live `HEAD`. The receipt separately records the clean live checkout
identity before and after archiving, so it proves source immutability without
requiring a later checkout's `HEAD` to remain pinned to the archived revision.

```bash
node eng/v0.2-baselines/generate-schema.mjs --check
node --test eng/v0.2-baselines/baseline.test.mjs
```

The archived source references retired GitHub NuGet/npm package identities.
Its dynamic measurement option is disabled so a local invocation cannot query
GitHub Packages. Current local validation uses the exact local feed and
registry setup recorded by the maintained package-consumer fixtures, together
with the package canaries in the owning repositories.

Every required metric has status `passed`, `failed`, or `blocked`; only an
all-passed receipt verifies. Static checks remain source evidence. The former
dynamic design is retained only in the archived receipt contract; it is not
runnable locally and must not be treated as a local validation procedure.

Verification is deliberately closed: it requires an explicitly supplied release
manifest from a clean release-automation checkout. It archives that checkout's exact
`HEAD`, validates the committed manifest with its committed schema and semantic verifier,
and records the resulting logical path, revision, tree, SHA-256 digest, and clean
before/after snapshots. The complete approved public NuGet/npm counts are derived only
from that validated committed blob. Those counts are separate from the 24
consumer-package pins metric, which remains local consumer evidence. It also requires
clean unchanged live source snapshots,
the full fixed package-identity sets, exact logical commands for every canary
and NativeAOT phase, and `file` plus `readelf` evidence for an ELF64 Linux
x86-64 artifact. Temporary archive, profile, and output paths are normalized
before they enter a receipt.

`baseline.schema.json` is generated from the Effect Schema contract in
`contract.mjs`; the verifier always loads that contract internally. Comparison
validates both receipts, stable-sorts object keys while preserving arrays, and
removes only the fixed build/reload/startup timing observation and summary
fields. There is intentionally no `current-baseline.json`: this archived graph
is not eligible to establish current local evidence. A current receipt needs a
new consumer graph whose pins are available from the local candidate feeds.
