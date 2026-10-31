linux-x64, linux 7.3.0, .NET SDK 10.0.403, NativeAOT; medians of 21 runs of `measure.mjs` (S1, 2026-10-31). The
synthetic frontend has 17 assets, 5.88 MiB, packed to 1.89 MiB; the first page loads the entry document, the entry
and vendor bundles and the stylesheet (3.8 MiB). Times are in-process from the start of `AssetArchive.ReadEmbedded`.

| Runic.Assets | Load (ms) | Load + first page (ms) | Every asset (ms) | Process wall time (ms) | Peak RSS (MiB) | OpenSSL loaded |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| 0.14 (main `11b487a93`): every asset inflated and SHA-256-checked at load | 15.85 | 15.87 | 15.88 | 20.62 | 26.0 | yes |
| 0.15 (S1): directory checked at load, each asset inflated on first open | 0.43 | 5.51 | 8.01 | 11.81 | 13.6 | no |
