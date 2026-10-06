# Runic.Assets package consumer

`Test-PackageConsumer.sh` packs `Runic.Assets`, `Runic.Assets.AspNetCore`,
`Runic.Assets.Desktop` and `Runic.Desktop` from this checkout into a temporary
feed. It restores this project into a fresh package cache from that feed plus
NuGet.org for NativeAOT runtime packs, publishes it, and runs an embedded asset
validation/open smoke test and packaged-packer checks. Run it from a clean clone:

```sh
bash tests/fixtures/assets/Runic.Assets.PackageConsumer/Test-PackageConsumer.sh
```

Pass a version and a directory of already packed `.nupkg` files to check
existing packages instead:
`Test-PackageConsumer.sh 0.7.0-preview.1 artifacts/packages/nuget`.
