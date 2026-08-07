# RunicToolkit.ApplicationBridge.Generators

Consumes committed `bridge.manifest.json` and JSON Schema files through MSBuild
`AdditionalFiles`. Compilation never starts Node or npm. Unsupported schema
constructs and stale fingerprints produce stable `RTKAB` diagnostics.
