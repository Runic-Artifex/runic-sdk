# Translations test fixture

`translated-text.esm.test.ts` resolves references with a real Runic Translations runtime:

- `catalog/` is a small catalog in English and German.
- `generated/` is its ESM output, generated with `runic-translations` 0.8.0:
  `dotnet tool run runic-translations -- generate --project catalog --output generated --emit-esm`.
- `producer-state.json` is the state the .NET producer writes for `TranslatedCatalogViewModel`
  (`TranslatedTextTests.TheProducersReferencesAreThePageFixture`, which `RUNIC_UPDATE_GOLDEN=1` rewrites).

Regenerate `generated/` when the catalog changes, and keep the catalog's contract fingerprint in
`TranslatedCatalogViewModel.Fingerprint`.
