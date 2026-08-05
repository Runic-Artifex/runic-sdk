# Getting started

Enter the pinned development environment and run the repository gate:

```bash
nix develop
./eng/verify.sh
```

For application code, configure the Runic Artifex GitHub NuGet and npm package
feeds, then reference the smallest package set required by the chosen host and
frontend. The initial prerelease workflow publishes version-matched Toolkit
NuGet packages and these npm packages:

- `@runic-artifex/mvvm`
- `@runic-artifex/mvvm-react`
- `@runic-artifex/mvvm-vue`
- `@runic-artifex/mvvm-svelte`
- `@runic-artifex/mvvm-angular`
- `@runic-artifex/mvvm-conformance`

Use [`runic-toolkit-examples`](https://github.com/Runic-Artifex/runic-toolkit-examples)
for runnable, package-only applications. Templates remain staged until the
independently owned Runic Markup integration packages have been published.
