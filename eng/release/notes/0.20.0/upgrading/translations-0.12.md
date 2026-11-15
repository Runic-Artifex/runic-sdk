- **Runic Translations 0.12** (the 0.20 train candidate).
  `Runic.Application.Views.Translations` requires Runic Translations
  `[0.12.0, 0.13.0)`, so an application that uses it moves every Translations
  package, NuGet and npm, to 0.12.0. The template pins Translations 0.12.0 three
  ways: the `dotnet-runic-translations` tool, `Runic.Translations` and
  `.Build`, and `@runic-artifex/vite-plugin-runic-translations`. With the plugin
  0.12, an edited message updates in place under `dotnet runic dev`, and Runic
  Inspect marks the page's messages and edits them; an earlier plugin reloads
  the page and offers no editing. Under `vite dev` the plugin answers
  `/__runic-translations/message`, which writes the project's `.rmf2` files, to
  the same machine only; `inPageEditing: false` turns it off. Runic Command Line
  stays at 0.13. See the
  [Translations 0.12.0 notes](https://github.com/Runic-Artifex/runic-translations-sdk/blob/v0.12.0/eng/release/notes/0.12.0.md#upgrading-from-011).
