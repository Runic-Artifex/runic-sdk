# Runic.Application.Views.Translations consumer

An application that uses the packed `Runic.Application.Views.Translations` package as an application would: German as
its language, its own wording of two `runic-views` messages, and French, which the package lacks, through `runic.json`
`overrides`
([Ship translations in a library](https://github.com/Runic-Artifex/runic-translations-sdk/blob/v0.11.0/docs/guides/translations/libraries.md#the-application)).
`bun run verify-packages Runic.Application.Views.Translations` copies this directory next to a project that references
the packed candidate and runs it; CI runs it with every package consumer.

- `translations/runic.json`: the application's catalog (`consumer`) and the override of `runic-views`.
- `translations/overrides/runic-views/en.rmf2`, `de.rmf2`: the application's text for single `runic-views` keys.
- `translations/overrides/runic-views/fr.rmf2`: adds French to `runic-views`, so `SupportedLocales` keeps the
  application's French; the messages it does not override show in English.
- `Program.cs`: registers both catalogs with `Runic.Translations.Hosting` and `AddRunicViewsTranslations()`, then checks
  the text in German and English, locale-first, and in French.
