# Portable conformance formats

Conformance uses deterministic JSON artifacts under version control. A runner
may be implemented in any language, but it must report the contract identity,
scenario or vector ID, implementation identity, supported capabilities, and
one pass, fail, unsupported, or excluded outcome.

## Behavioral scenarios

Behavioral scenarios validate lifecycle, requests, ordering, cancellation,
streaming, security, and errors against
[`scenario.schema.json`](scenario.schema.json).

- `arrange` creates deterministic resources without asserting behavior.
- `steps` performs ordered semantic operations. Operation names belong to the
  scenario format, not to a language API.
- `expect.events` is the exact ordered sequence of normative events.
  Implementation diagnostics are ignored.
- `expect.states` names required terminal resource states.
- `expect.outcomes` contains at most one terminal outcome per invocation or
  request identifier.
- `expect.absentEvents` lists events that must never occur.

Scenarios inject identifiers, clock values, limits, and policy. They never use
wall-clock sleeps, ephemeral ports, random credentials, CLR exception names,
Effect internals, browser process identifiers, or physical packet boundaries.

## Codec vectors

Protocol and serialization use deterministic input/output vectors validated by
[`codec-vector.schema.json`](codec-vector.schema.json). A vector names its
versioned profile and operation, encodes bytes as base64, and expects either one
canonical value or one contract error with category, code, safe message, and
retryability.

Wire-profile vectors may describe `webui-compat/52f9e75` without making that
profile the Runic Desktop product contract. A future Runic-owned profile adds
its own vectors and version.

## Evidence separation

Portable scenarios and vectors are normative. The following are retained
outside this directory as implementation evidence:

- .NET public API, trimming, NativeAOT, Kestrel, browser, and WebView results;
- TypeScript type checks, Effect scope/interruption tests, and browser results;
- Windows, Linux, and macOS process smokes;
- CS-WebUI differential classifications; and
- performance and accessibility measurements.

The .NET M6 receipt is retained at
[`evidence/conformance/dotnet-m6.json`](../../evidence/conformance/dotnet-m6.json).
CS-WebUI comparisons are classified in
[`cs-webui-classification.md`](cs-webui-classification.md), and the native
runner definition and current local evidence are recorded in
[`evidence/platform-matrix.md`](../../evidence/platform-matrix.md).

The closed example-parity catalog is retained in
[`cs-webui-example-parity.json`](cs-webui-example-parity.json). It maps every
example in the pinned upstream WebUI corpus plus every sample on the maintained
CS-WebUI `main` revision to a Desktop sample, automated behavioral equivalent,
suite-level replacement, or documented non-goal. Contract verification rejects
missing, duplicate, unknown, or dangling mappings.

An unsupported scenario is acceptable only when its capability is explicitly
optional or the implementation profile is excluded. A required v1 capability
reported as unsupported fails its milestone gate.
