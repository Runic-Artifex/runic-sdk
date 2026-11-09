# Translations test fixture

`translated-text.esm.test.ts` resolves references with a real Runic Translations runtime:

- `catalog/` is a small application catalog (`notes`) in English and German.
- `library/` is a component library's catalog (`acme.grid`) in English, German and Arabic. The test resolves
  references of both catalogs with `createReferenceResolver`, and the Arabic locale checks right-to-left placeholder
  isolation.
- `generated/` is their ESM output, generated with `runic-translations` 0.9.1, one catalog at a time:

  ```sh
  dotnet tool install dotnet-runic-translations --version 0.9.1 --tool-path <dir>
  <dir>/runic-translations generate --project catalog --output generated --emit-esm
  <dir>/runic-translations generate --project library --output generated --emit-esm
  ```

  Regenerate the whole folder with one release; never mix files of two releases.
- `producer-state.json` is the state the .NET producer writes for `TranslatedCatalogViewModel`
  (`TranslatedTextTests.TheProducersReferencesAreThePageFixture`, which `RUNIC_UPDATE_GOLDEN=1` rewrites).

Regenerate `generated/` when a catalog changes, and keep the catalogs' contract fingerprints in
`TranslatedCatalogViewModel.Fingerprint` and `TranslatedCatalogViewModel.LibraryFingerprint`.
