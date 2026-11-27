- **Runic Translations 0.16 and Runic Command Line 0.15.**
  `Runic.Application.Views.Translations` requires Runic Translations
  `[0.16.0, 0.17.0)`, so an application that uses it moves every Translations
  package, NuGet and npm, to 0.16.0. The template pins Translations 0.16.0 three
  ways: the `dotnet-runic-translations` tool, `Runic.Translations` and
  `.Build`, and `@runic-artifex/vite-plugin-runic-translations`; `dotnet runic
  doctor` checks for Translations 0.16.0 and Command Line 0.15.0. Runic's own
  catalog and the ESM output of 0.16 are those of 0.15, so pages and generated
  modules are unaffected.
  `dotnet-runic`, `Runic.Create` and the `Runic.Assets` packer bundle Runic
  Command Line 0.15.0. An application that uses Command Line itself moves to
  0.15.0 with Translations 0.16, whose `Runic.Translations.CommandLine` requires
  `[0.15.0, 0.16.0)`. Command Line 0.15 adds catalog validation codes
  (`RCLI0028` to `RCLI0030`, a stricter `RCLI0019`), leaves `--version` out of a
  command's and a group's help (root help keeps it), ends a root command's usage
  error with root help's usage line, and no longer suggests a root command's
  name for a mistyped command. Update tests that compare such help or standard
  error text. The tools' own help and errors change the same way; none of them
  needed another change. See the
  [Translations 0.16.0 notes](https://github.com/Runic-Artifex/runic-translations-sdk/blob/v0.16.0/eng/release/notes/0.16.0.md#upgrading-from-015)
  and the
  [Command Line 0.15.0 release](https://github.com/Runic-Artifex/runic-cli-sdk/releases/tag/v0.15.0).
