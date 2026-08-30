# W40 MF2-subset package consumer

This exact-local fixture installs one Runic Translations candidate family and
one Vite-plugin archive into fresh NuGet and npm caches twice. It generates one
closed `runic-mf2-subset/1` catalog, compares its typed C# and ESM results, and
rejects unsupported full MF2 plus ABI, schema, stale-asset, and forged-manifest
skew before writing a receipt.

Provide a local feed containing the exact `Runic.Translations`,
`Runic.Translations.Build`, `Runic.Translations.Generator`, and
`Runic.Translations.Tool` candidates (and their tool dependencies), plus a
locally packed Vite-plugin archive:

```bash
RUNIC_W40_NUGET_FEED=/absolute/path/to/feed \
RUNIC_W40_NPM_ARCHIVE=/absolute/path/to/vite-plugin.tgz \
node eng/current-mf2-subset-consumer/verify.mjs run-twice > w40-receipt.json
node eng/current-mf2-subset-consumer/verify.mjs verify-twice w40-receipt.json
sha256sum w40-receipt.json
```
