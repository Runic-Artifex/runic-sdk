# Development

Use the pinned Nix environment:

```bash
nix develop
./eng/verify.sh
```

The verification script checks identities, solution completeness, ownership,
npm lock restoration, TypeScript packages, .NET compilation, executable
contract suites, NativeAOT, browser behavior, package consumers, and generated
templates against the exact compatibility set.

Before pushing release-pipeline changes, run the same release-candidate command
used by the prerelease and public-release workflows:

```bash
version="$(node eng/compatibility-set-value.mjs release-train-version)"
output="$(mktemp -d /tmp/runic-toolkit-candidate.XXXXXXXXXX)"
./eng/verify-release-candidate.sh "$version" "$output" github
```

The command uses detached compatibility revisions, a fresh npm cache, a
separate exact NuGet dependency feed, isolated NuGet/npm consumers, NativeAOT,
and cold local-registry template acceptance. Use `public` instead of `github`
to validate public NuGet/npm metadata with the exact compatibility-authority
validator used in CI.

## Run the GitHub Actions job locally

On Linux, the repository can execute the real `verify` job in a pinned Ubuntu
24.04 runner container before pushing:

```bash
systemctl --user start podman.socket
./eng/run-ci-local.sh
```

The wrapper obtains `act` from the repository's locked Nixpkgs input, copies the
current working tree into the container, and prints the total elapsed time. It
always uses a synthetic fork pull-request event, which runs verification but
cannot enter the workflow's candidate publication or registry round-trip
steps. A GitHub token is still required for read-only action and private
package downloads; set `GITHUB_TOKEN`, or authenticate `gh` locally.

The first invocation builds a small repository runner layer with PowerShell and
Chrome's Linux libraries on the pinned base image, then downloads the actions.
Use `./eng/run-ci-local.sh --offline` after they are cached, or `--dry-run` to
validate workflow expansion without running the job. The Windows job remains a
real Windows-runner gate and is not emulated by this wrapper.
