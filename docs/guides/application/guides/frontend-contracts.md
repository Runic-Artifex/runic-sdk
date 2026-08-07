# Frontend contracts

Application Bridge contracts begin as Effect Schemas containing named domain
commands, receipts, snapshots, events, and sanitized errors. Run
`eng/generate-application-bridge-contract.mjs` to emit deterministic JSON Schema
files and `bridge.manifest.json`, then commit those artifacts.

Reference `RunicToolkit.ApplicationBridge.Generators` as an analyzer and pass
the manifest and schemas as `AdditionalFiles`. C# compilation reads only those
files; it never starts Node. The generator emits closed wire records, a typed
handler interface, exhaustive reflection-free dispatch, typed event publisher
extensions, and embedded fingerprints. Unsupported schema constructs fail with
an `RTKAB` diagnostic.

Application handlers remain handwritten because navigation, authorization,
operations, persistence, and destructive actions are domain policy.
