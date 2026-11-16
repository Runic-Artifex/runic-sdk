- **Runic Translations 0.13** (the 0.21 train candidate).
  `Runic.Application.Views.Translations` requires Runic Translations
  `[0.13.0, 0.14.0)`, so an application that uses it moves every Translations
  package, NuGet and npm, to 0.13.0. The template pins Translations 0.13.0 three
  ways: the `dotnet-runic-translations` tool, `Runic.Translations` and
  `.Build`, and `@runic-artifex/vite-plugin-runic-translations`. Translations
  0.13 renames `TranslationContextBuilder.Add` to `AddCatalog`: replace `.Add(`
  with `.AddCatalog(` where a `TranslationContext.CreateBuilder()` chain
  registers a catalog, as the template's `WorkspaceServices.cs` now does. The
  in-page editing API reads only `RUNIC_DEV_ALLOW`, which vite-plugin-runic
  reads too; `RUNIC_TRANSLATIONS_EDIT_ALLOW` is ignored. Runic Command Line
  stays at 0.13. See the
  [Translations 0.13.0 notes](https://github.com/Runic-Artifex/runic-translations-sdk/blob/v0.13.0/eng/release/notes/0.13.0.md#upgrading-from-012).
