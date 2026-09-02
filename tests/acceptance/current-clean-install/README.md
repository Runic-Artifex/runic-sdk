# Current clean-room Desktop journey

This is the current package-only Svelte Desktop golden path. It is not a
v0.2 receipt and never reads an adjacent legacy package directory.

The journey needs five explicit inputs: the canonical compatibility set, a
directory containing the exact selected NuGet packages, the Runic npm registry,
and the two explicit public package sources required for framework dependencies.
It rejects a missing package, a non-exact pre-release version, GitHub Packages,
ambient caches, and generated project pins that differ from the compatibility
set before accepting any receipt. npm installation uses `npm ci`; every Runic
lock integrity must match metadata read from the explicit Runic registry even
when npm's lockfile v3 omits redundant `resolved` URLs.

It installs the template and `dotnet-runic` from the supplied local feed, creates
the Svelte template, installs its frontend, typechecks and builds it, restores
and builds the managed host, runs `doctor`, `inspect`, and a dry-run `dev`,
publishes the application, and performs two headless `--smoke-test` runs to
prove both initial launch and restart. Every phase must pass twice with
byte-identical receipts.

```bash
node eng/current-clean-install/verify.mjs run-twice \
  --compatibility-set ../.github/runic.compatibility-set.json \
  --nuget-feed /absolute/path/to/runic-1.0-preview.1-nuget-feed \
  --npm-registry http://127.0.0.1:4873 \
  --npm-public-registry https://registry.npmjs.org \
  --nuget-public-source https://api.nuget.org/v3/index.json
```

The supplied local feed is deliberately producer-owned. This repository does
not build product sources or silently choose a sibling checkout; it only proves
what a consumer can do with the supplied package train on the current platform.
