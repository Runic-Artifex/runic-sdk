# Getting started

Enter the pinned development environment and run the repository gate:

```bash
nix develop
./eng/verify.sh
```

For application code, configure the Runic Artifex GitHub NuGet and npm package
feeds, then reference the smallest package set required by the chosen host and
frontend. The initial prerelease workflow publishes version-matched Toolkit
NuGet packages and the framework-neutral npm runtime:

- `RunicToolkit.ApplicationBridge`
- `RunicToolkit.ApplicationBridge.Generators`
- `RunicToolkit.Hosting.CsWebUi.ApplicationBridge`
- `@runic-artifex/application-bridge`

Use [`runic-toolkit-examples`](https://github.com/Runic-Artifex/runic-toolkit-examples)
for runnable, package-only applications. The React, Vue, Svelte, and Angular
templates exercise the same bridge through each renderer without a
renderer-specific Toolkit adapter.
