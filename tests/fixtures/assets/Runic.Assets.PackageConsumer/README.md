# Runic.Assets package consumer

`Test-PackageConsumer.sh` packs `Runic.Assets`, restores this project into
a fresh package cache from the temporary feed plus NuGet.org for NativeAOT runtime
packs, publishes it, and runs an embedded asset validation/open smoke test.
