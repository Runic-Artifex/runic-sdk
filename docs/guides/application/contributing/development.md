# Development

Use the pinned Nix environment:

```bash
nix develop
./eng/verify.sh
```

The verification script checks identities, solution completeness, ownership,
npm lock restoration, TypeScript packages, .NET compilation, and executable
contract suites. It deliberately excludes the real-browser native canary and
templates acceptance test: the former needs the pinned cs-webui native library
and Chromium, while the latter activates only after external integration
packages are published.

To validate release artifacts locally:

```bash
./eng/pack.sh 0.1.0-preview.local.1 /tmp/runic-toolkit-packages
./tests/RunicToolkit.PackageCanary/Test-PackageCanary.sh \
  0.1.0-preview.local.1 /tmp/runic-toolkit-packages
node eng/pack-npm.mjs 0.1.0-preview.local.1 /tmp/runic-toolkit-packages
node eng/verify-npm-artifacts.mjs 0.1.0-preview.local.1 /tmp/runic-toolkit-packages
```
