# Development

Work from the `runic-sdk` root in the pinned Nix environment:

```sh
nix develop
bun run bootstrap
bun run build
bun run ci --list
bun run ci --job managed --matrix suite:application
```

`RunicSdk.Core.slnx` contains SDK libraries, tools and managed tests;
`RunicSdk.slnx` also includes the editor and current applications. Build commands
use Debug by default; the CI workflow uses Release. First-party tools use Bun.
Node and npm/pnpm are retained for explicit package-manager compatibility checks.

## Run GitHub Actions locally

On Linux, enable Docker or the rootless Podman socket, then run the actual SDK
workflow in its Ubuntu runner container:

```sh
systemctl --user start podman.socket
bun run ci
bun run ci --job customers --matrix journey:dev
bun run ci --job templates
```

The Nix flake supplies the compatible `act` runner. Each invocation snapshots
uncommitted source once, then executes the workflow's jobs and dependencies against
that snapshot. Build artifacts and dependency downloads are shared through local
servers. Windows and macOS checks still require native GitHub runners.

See the [local CI guide](../../../../eng/ci/README.md) for prerequisites, caches,
artifacts, job selection and reruns. The imported multi-repository release scripts
are historical evidence under `eng/archive`; they are not the current SDK gate.
