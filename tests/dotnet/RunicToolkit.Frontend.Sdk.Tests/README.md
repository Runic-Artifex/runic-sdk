# RunicToolkit.Frontend.Sdk tests

This executable suite exercises the frontend contract compiler and the SDK's
MSBuild configuration validation without npm installs or network access.

It verifies deterministic generation, current and stale `--verify` behavior,
schema validation diagnostics, valid/invalid SDK property combinations,
lock-file-identity install caching, cross-process serialization for projects
sharing one workspace, and the Vite development-build opt-out in temporary
workspaces. The install and build lifecycle tests use local fake commands, so
the suite performs no npm installs or network access.
