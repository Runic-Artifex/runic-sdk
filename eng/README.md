# SDK engineering

`workspace.json` defines package identities, artifact paths and the component
map. `run.mjs` supplies build and packaging commands. `test.mjs` selects focused
local checks; `.github/workflows/ci.yml` owns complete verification on GitHub.

- `build/`: shared managed build policy.
- `ci/`: workflow support, reusable outputs and optional local execution with act.
- `release/`: package inspection, trusted publication and public-install smoke.
- `dependencies/`: dependency audit and template lock maintenance.
- `toolchain.mjs`: SDK and package-manager pins.
- `bridge/`: generated application contracts and template checks.
- `reliability/`: optional performance measurements and lifecycle investigations.

Use project and workspace references for source development. CI installs packed
artifacts outside the checkout to exercise consumer behavior and provider isolation.
Templates retain independent locks to check fresh applications.

See the [release guide](release/README.md) for current publication policy. The
release workflow runs CI, publishes those artifacts, checks public installation
and creates the GitHub prerelease. Historical first-preview evidence does not add
requirements to subsequent releases.
