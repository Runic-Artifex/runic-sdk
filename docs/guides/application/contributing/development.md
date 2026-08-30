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
