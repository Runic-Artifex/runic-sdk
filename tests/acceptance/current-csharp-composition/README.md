# Current C# composition fixture

This additive fixture proves the current package-only C# base profile without
altering frozen `samples/01` through `samples/05`. It restores exact
`Runic.Application` and `Runic.Application.Testing` candidates from an
explicit isolated candidate feed, generates one `runic.application/1` manifest, and
runs it through the public deterministic headless host.

Each journey uses disposable NuGet package and HTTP caches, verifies restored
package source metadata and committed release authority facts, and compiles
missing, duplicate, and invalid manifest declarations that must fail closed.
Two complete journey receipts must match exactly.

Run it from this repository's devshell:

```bash
RUNIC_CURRENT_CSHARP_CANDIDATE_FEED=/path/to/feed \
nix develop -c node eng/current-csharp-composition/verify.mjs run-twice ../.github/runic.release.json
```

This is headless/package-only conformance evidence only. It does not open a
native window, exercise a browser or HMR, migrate desktop capabilities, or
certify platform coverage.
