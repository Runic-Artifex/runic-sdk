# SDK engineering

`workspace.json` defines package identities, artifact paths and the component
dependency graph. `run.mjs` supplies focused build and packaging commands.
Verification is defined in `.github/workflows/ci.yml`; [local CI](ci/README.md)
runs that same workflow with `act`.

- `build/`: shared managed build policy.
- `ci/`: workflow support, reusable outputs and local execution.
- `release/`: candidate sealing, acceptance validation and exact-artifact publication.
- `dependencies/`: dependency audit and template lock maintenance.
- `toolchain.mjs`: reads the current SDK and package-manager pins.
- `bridge/`: generated application contracts and template checks.
- `reliability/`: matched measurements and native lifecycle soak tests.

Use project and workspace references for source development. Package acceptance
installs exact candidate artifacts outside the checkout and checks optional-provider
isolation. Templates keep independent dependency locks to verify fresh applications.

The [release guide](../docs/guides/releases/0.2.0-preview.1.md) and
[human acceptance instructions](preview-human-acceptance.md) describe the preview.
Select a successful full CI run for the frozen source, verify its packages and
required acceptance receipts, and publish those exact artifacts through the
separate `publish-preview.yml` workflow. Ordinary CI never publishes.

Registry access and selected-file/clipboard checks on the local Linux desktop and
Windows VM require user participation. Native JIT and AOT CI cover Linux x64,
Windows x64 and macOS arm64. Deferred human accessibility, macOS and additional
Linux display profiles are listed explicitly in the release policy; they are not
passing results. Public installation smoke checks follow registry publication.
