# WebUIToolkit.Frontend.Sdk tests

This executable suite exercises the frontend contract compiler and the SDK's
MSBuild configuration validation without npm installs or network access.

It verifies deterministic generation, current and stale `--verify` behavior,
schema validation diagnostics, and valid/invalid SDK property combinations in
temporary workspaces.
