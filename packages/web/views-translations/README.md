# @runic-artifex/views-translations

Optional English and German text for Runic's own page messages: field errors (a
number, date or time field that cannot read the user's text) and the validation
texts that the NuGet package
[`Runic.Application.Views.Translations`](../../dotnet/Runic.Application.Views.Translations/README.md)
gives the built-in DataAnnotations attributes. It is the page side of the
`runic-views` [Runic Translations](https://github.com/Runic-Artifex/runic-translations-sdk)
library catalog: prebuilt ES modules that need no Runic Translations package and
follow the window's language.

```sh
npm install @runic-artifex/views-translations
```

It requires the `@runic-artifex/views` of its release.

## Field errors

Once, in `main.ts`:

```ts
import { configureRunicViewsText } from '@runic-artifex/views-translations';

configureRunicViewsText();
```

`FieldState.error`, `resolveText`, the framework helpers (`useText`, `runicText`)
and `describeBridgeError` then show field errors in the window's language, such as
`1e ist keine Zahl.` Errors of other kinds, and reasons a later `@runic-artifex/views`
adds, keep their English message. An application with field error texts of its
own calls `runicViewsFieldErrorText` from its hook instead:

```ts
configureFieldErrorText((error, locale) => appFieldErrorText(error, locale) ?? runicViewsFieldErrorText(error, locale));
```

## Validation texts

The validation errors of the built-in attributes carry `runic-views` references.
Add the catalog to the reference resolver of your page's Translations runtime,
next to your own catalog:

```ts
import { configureTranslatedText } from '@runic-artifex/views';
import { runicViewsCatalog } from '@runic-artifex/views-translations';
import { m } from 'virtual:runic-translations/app';
import * as transport from 'virtual:runic-translations/app/transport';

const resolveReference = transport.createReferenceResolver([{ transport, messages: m }, runicViewsCatalog]);
configureTranslatedText({
  ...locale,
  resolve: (reference, language) => resolveReference(reference, { locale: language }).text,
  direction: localeDirection,
});
```

`resolveRunicViewsText(reference, locale)` formats a `runic-views` reference
alone, and returns `undefined` for a reference of another catalog. A page that
cannot resolve a reference shows its English fallback.

## Change single messages

The package declares its catalog (`runicTranslations` in `package.json`, and the
`./runic-translations` export), so the Vite plugin of Runic Translations with
`dependencies: 'auto'` regenerates it with the application's `runic.json`
`overrides` and languages, the same overrides the .NET build applies:

```ts
runicTranslations({ project: 'translations', dependencies: 'auto' })
```

```json
"overrides": [{ "catalog": "runic-views", "from": ["Runic.Application.Views.Translations", "@runic-artifex/views-translations"] }]
```

The catalog has English and German. A language of your page that it lacks gets
an override file, `translations/overrides/runic-views/<locale>.rmf2`, with the
messages you translate; the others show in English. Without one, Runic's text in
that language is English. See
[Change single messages](../../dotnet/Runic.Application.Views.Translations/README.md#change-single-messages),
[Your other languages](../../dotnet/Runic.Application.Views.Translations/README.md#your-other-languages)
and the list of keys in the NuGet package's README. CI builds a page with an
English override and added French against the packed package
([fixture](../../../tests/fixtures/application/views-translations-vite/README.md)).

## Exports

| Export | What it is |
| --- | --- |
| `configureRunicViewsText()` | `configureFieldErrorText(runicViewsFieldErrorText)`; returns a function that removes it |
| `runicViewsFieldErrorText(error, locale)` | The text of a field error, or `undefined` |
| `runicViewsCatalog` | `{ transport, messages }` for `createReferenceResolver` |
| `resolveRunicViewsText(reference, locale?)` | A `runic-views` reference's text, or `undefined` |
| `runicViewsMessages`, `runicViewsTransport` | The catalog's generated `m` and transport module |
| `./runic-translations` | The prebuilt catalog's web module manifest |

## Build

`bun run build` copies the catalog's sources from
`packages/dotnet/Runic.Application.Views.Translations/translations` to
`dist/catalog` and generates `dist/translations` with the `runic-translations`
tool pinned in `.config/dotnet-tools.json`, the release of the
`Runic.Translations.Build` that compiles the NuGet package's catalog
(`eng/workspace.test.mjs` checks that they agree), so the NuGet and npm packages
ship one catalog. Licensed under the [MIT License](LICENSE).
