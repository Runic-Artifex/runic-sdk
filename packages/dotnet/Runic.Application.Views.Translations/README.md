# Runic.Application.Views.Translations

Optional English and German text for Runic's own messages: validation messages of
the built-in DataAnnotations attributes, field errors, Linux dialog titles, the
suggested "Untitled" file name, shortcut key names, the error pages of a Runic
Desktop browser window and the standard macOS menus. It is a
[Runic Translations](https://github.com/Runic-Artifex/runic-translations-sdk)
library catalog, `runic-views`, so it follows the application's locale and an
application changes single messages, or adds a language, in its own `runic.json`.

The core packages keep no Runic Translations dependency: without this package,
Runic shows its English text and takes yours through the same hooks
(`RunicValidationOptions.TextFor`, `DesktopPlatformTexts`,
`DesktopHost.SetStandardMenuTitles`, `configureFieldErrorText`). The page side is
the npm package
[`@runic-artifex/views-translations`](../../web/views-translations/README.md),
built from the same catalog.

## Install

```sh
dotnet add package Runic.Application.Views.Translations
npm install @runic-artifex/views-translations
```

The package depends on `Runic.Translations` `[0.9.1, 0.10.0-0.0)` (before its
1.0, a Runic Translations minor may break), `Runic.Translations.Hosting` 0.9.1 or
later, and the `Runic.Application.Views` of its release.

**It has English and German only, so your language list shrinks to them.**
`ITranslationContext.SupportedLocales` lists the locales in which *every*
catalog shows its own text, so with this package it lists at most `en` and `de`.
A language picker built from it loses your other languages, and negotiating
against it (`LocaleNegotiation.Negotiate(DesktopLanguages.Preferred,
translations.SupportedLocales, …)`, as the
[runic-views guide](https://github.com/Runic-Artifex/runic-translations-sdk/blob/v0.9.1/docs/guides/translations/runic-views.md#4-the-applications-language)
does) picks English for a French user. Add one override file per extra
language; see [Your other languages](#your-other-languages). The first window
logs the languages that need one (event 1261).

## Use it

Register it next to your own catalog. With `Runic.Translations.Hosting`:

```csharp
using Runic.Application.Views.Translations;
using Runic.Translations.Hosting;

services.AddRunicWindowService();
services.AddRunicTranslations(options => options.InitialLocale = settings.Language)
    .AddCatalog(AppTextCatalog.Registration, context => new AppText(context));
services.AddRunicViewsTranslations();   // Runic's own text, in the same context
```

and let the context follow the application's locale, as for any catalog:

```csharp
var locale = provider.GetRequiredService<IRunicApplicationLocale>();
locale.Follow(provider.GetRequiredService<ITranslationContext>().SetLocaleAsync);
await locale.SetLocaleAsync("de");
```

`AddRunicViewsTranslations()`:

- adds the `runic-views` catalog to the shared context, so it switches with
  yours;
- registers `RunicValidationOptions` with `TextFor = RunicViewsTranslations.ValidationText`
  unless the application registers its own; the page shows the messages in the
  window's language;
- keeps `DesktopPlatformTexts.Current` in the context's locale from the first window
  on (dialog titles, "Untitled", the keyring label, Ctrl/Shift/Alt in shortcut
  labels, the error pages of a Runic Desktop browser window; CS-WebUI's own
  pages stay English, see
  [Runic's own text](../Runic.Application.Views.CsWebUi/README.md#runics-own-text)).
  A switch changes them before
  `IRunicApplicationLocale.LocaleChanged` is raised. A text the platform refuses,
  such as an overridden file name with `/`, is logged as event 1260
  `PlatformTextsRejected` (category `Runic.Application.Views.Translations`) and the
  previous texts stay;
- logs, when the first window opens, the locales your catalogs have and this one
  lacks, as event 1261 `LocalesWithoutRunicViewsText`.

With a scoped context (`options.Lifetime = ServiceLifetime.Scoped`, a context
per window or request), the platform texts, which are process-wide, follow an
application-wide context of their own: the package creates it with
`ITranslationContextFactory` in `IRunicApplicationLocale`'s locale and lets it
follow that locale. It never resolves the scoped context from the root provider,
so `ValidateScopes` (the Development default of `Host.CreateApplicationBuilder`)
is fine.

On the page, register the catalog's references and field errors once, in
`main.ts` (see the [npm package](../../web/views-translations/README.md)):

```ts
import { configureRunicViewsText, runicViewsCatalog } from '@runic-artifex/views-translations';

configureRunicViewsText();   // field errors in the window's language
const resolveReference = transport.createReferenceResolver([{ transport, messages: m }, runicViewsCatalog]);
```

### Your own validation texts too

An application that registers its own `RunicValidationOptions` calls this
package's texts last:

```csharp
services.AddSingleton(new RunicValidationOptions
{
    TextFor = (attribute, member) => AppValidationText(attribute, member)
        ?? RunicViewsTranslations.ValidationText(attribute, member),
});
```

`ValidationText` covers `[Required]`, `[MaxLength]`, `[MinLength]`,
`[StringLength]`, `[Length]`, `[Range]` with inclusive limits (whole numbers,
decimals and dates; numbers are formatted in the window's language),
`[EmailAddress]`, `[Phone]`, `[Url]`, `[CreditCard]`, `[RegularExpression]`,
`[Compare]`, `[FileExtensions]`, `[Base64String]`, `[AllowedValues]`,
`[DeniedValues]` and `[EnumDataType]`. It returns `null`, so the attribute's own
message stays, for an attribute with an `ErrorMessage` or message resource of its
own, a type derived from them, a `[Range]` with an exclusive limit, or limits that
are no number or date. The field's name is the member's display name, as written
in C#. `ValidationReference` returns the same text as a `TranslationReference`.

### Standard macOS menus

Runic Desktop's standard menus take their titles from
`DesktopHost.SetStandardMenuTitles`, a `Runic.Desktop` type that this package
does not reference, so that CS-WebUI applications do not depend on Runic Desktop.
`RunicViewsTranslations.MenuTitles(context)` returns them in the context's
locale, with members named as `DesktopStandardMenuTitles`'s; set them in your
`LocaleChanged` handler. `{0}` stays for the application's name, which Runic
inserts:

```csharp
static DesktopStandardMenuTitles StandardMenuTitles(RunicViewsMenuTitles titles) => new()
{
    About = titles.About, Hide = titles.Hide, HideOthers = titles.HideOthers, ShowAll = titles.ShowAll, Quit = titles.Quit,
    File = titles.File, Close = titles.Close, OpenRecent = titles.OpenRecent, ClearMenu = titles.ClearMenu, Edit = titles.Edit,
    Undo = titles.Undo, Redo = titles.Redo, Cut = titles.Cut, Copy = titles.Copy, Paste = titles.Paste, SelectAll = titles.SelectAll,
    View = titles.View, ActualSize = titles.ActualSize, ZoomIn = titles.ZoomIn, ZoomOut = titles.ZoomOut, Window = titles.Window,
    Minimize = titles.Minimize, Zoom = titles.Zoom, BringAllToFront = titles.BringAllToFront,
};

locale.LocaleChanged += (_, _) => desktopHost.Desktop.SetStandardMenuTitles(
    StandardMenuTitles(RunicViewsTranslations.MenuTitles(provider.GetRequiredService<ITranslationContext>())));
```

### Without dependency injection

```csharp
TranslationContext translations = TranslationContext.CreateBuilder()
    .Add(AppTextCatalog.Registration)
    .Add(RunicViewsTranslations.CatalogRegistration)
    .Build("de");

var validation = new RunicValidationOptions { TextFor = RunicViewsTranslations.ValidationText };
using var platformTexts = RunicViewsTranslations.FollowPlatformTexts(translations);
```

`RunicViewsTranslations.PlatformTexts(context)` returns the texts without setting
them. `FollowPlatformTexts` takes the context whose locale they should have: one
of the application's language, as `DesktopPlatformTexts` is process-wide.

### What follows which locale

- **A window with its own locale** (`IRunicWindowLocale.Locale = "de"` in an
  English application) shows its validation messages and field errors in German,
  but Linux dialog titles, the suggested file name, Windows shortcut labels and
  the other `DesktopPlatformTexts` stay in the application's locale: they are
  process-wide.
- **Menu bars keep their shortcut labels after a switch.** A Windows or in-page
  menu bar writes `Ctrl`/`Strg` when it is set, so after a language switch it
  keeps the old key names until the application sets the menu bar again. Rebuild
  your menu bar in `IRunicApplicationLocale.LocaleChanged`, as for its own
  titles; the platform texts have changed by then.

## Your other languages

An application with French and Spanish too adds them to `runic-views` with one
override file each, in its `runic.json` project (see
[Change single messages](#change-single-messages) for the `overrides` entry). A
file holds the messages you translate; the others show in English:

```text
translations/
  runic.json                          "locales": ["en", "de", "fr", "es"], and the runic-views override
  overrides/runic-views/fr.rmf2
  overrides/runic-views/es.rmf2
```

```rmf2
# overrides/runic-views/fr.rmf2
platform {
  untitled_file_name = Sans titre
  save_file_title = Enregistrer le fichier
}
```

`SupportedLocales` then lists `de`, `en`, `es` and `fr` again. Instead, an application may negotiate
against its own catalog's locales (`translations.Catalogs[i].SupportedLocales`)
and accept English Runic text in the others. The
[consumer fixture](../../../tests/fixtures/application/views-translations-consumer/README.md)
adds French this way, and CI checks it against the packed package.

## Change single messages

The package packs its catalog (`runic/runic-views/`), so an application overrides
single messages, or adds a language, in its own `runic.json`. One file serves the
.NET build and the Vite build of the page:

```json
"overrides": [{ "catalog": "runic-views", "from": ["Runic.Application.Views.Translations", "@runic-artifex/views-translations"] }]
```

```text
translations/
  runic.json
  overrides/runic-views/en.rmf2     platform { untitled_file_name = New document }
  overrides/runic-views/fr.rmf2     a language the package lacks
```

Register the overrides with the catalogs: `.AddOverlays(AppTextCatalog.Overlays)`
on the Hosting builder or the `TranslationContext` builder. Overrides apply
locale-first: a German user sees this package's German text where you override
only the English one. `runic-translations overrides add runic-views --locale en --key platform.untitled_file_name`
scaffolds an override. See
[Ship translations in a library](https://github.com/Runic-Artifex/runic-translations-sdk/blob/v0.9.1/docs/guides/translations/libraries.md#the-application)
and the [consumer fixture](../../../tests/fixtures/application/views-translations-consumer/README.md),
which CI builds against the packed package.

## Keys

Keys and their inputs are API: an override names them, and the build checks it.
A message whose meaning changes gets a new key; removed keys are listed in the
release notes.

| Group | Keys | Used by |
| --- | --- | --- |
| `validation` | `required`, `max_length`, `min_length`, `length_between`, `range_integer`, `range_number`, `range`, `email`, `phone`, `url`, `credit_card`, `pattern`, `compare`, `file_extensions`, `base64`, `allowed_values`, `denied_values`, `enum` | `ValidationText` (input `field`, and `min`, `max`, `other`, `extensions`) |
| `field` | `required.number`, `required.whole_number`, `required.date`, `required.time`, `required.date_time`, `not_a_number`, `not_a_whole_number`, `too_many_fraction_digits`, `out_of_range`, `not_a_boolean`, `not_a_choice`, `not_a_date`, `not_a_date_in`, `not_a_time`, `not_a_time_in`, `not_a_date_time`, `offset_out_of_range`, `no_text_form` | the page's field errors (input `text`, and `scale`; the lists `choices` and `patterns`, joined as alternatives) |
| `platform` | one per `DesktopPlatformTexts` text, such as `untitled_file_name`, `save_file_title`, `control_key`, `access_denied_title`, `loading_title` | `PlatformTexts`, `FollowPlatformTexts` |
| `menu` | one per `DesktopStandardMenuTitles` title, such as `about`, `quit`, `edit`, `select_all` (input `app` for `about`, `hide`, `quit`) | `MenuTitles`, the standard macOS menus |

The English text is the English that Runic shows without the package. The
generated catalog classes are internal; `RunicViewsTranslations.CatalogId` and
`CatalogRegistration` are the catalog's public members.

## Compatibility and status

This package targets .NET 10 and is versioned with the Runic SDK. Licensed under
the [MIT License](../../../LICENSE).
