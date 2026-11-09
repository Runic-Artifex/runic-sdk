# Runic.Application.Views.Translations

Optional English and German text for Runic's own messages: validation messages of
the built-in DataAnnotations attributes, field errors, Linux dialog titles, the
suggested "Untitled" file name, shortcut key names, a browser window's error pages
and the standard macOS menus. It is a
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

The package depends on `Runic.Translations` and `Runic.Translations.Hosting`
`[0.9.1, 0.10.0-0.0)` and on the `Runic.Application.Views` of its release.

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
  yours, and registers `RunicViewsText`, its generated text class;
- registers `RunicValidationOptions` with `TextFor = RunicViewsTranslations.ValidationText`
  unless the application registers its own; the page shows the messages in the
  window's language;
- keeps `DesktopPlatformTexts.Current` in the context's locale from the first window
  on (dialog titles, "Untitled", the keyring label, Ctrl/Shift/Alt in shortcut
  labels, a browser window's error pages). A switch changes them before
  `IRunicApplicationLocale.LocaleChanged` is raised. A text the platform refuses,
  such as an overridden file name with `/`, is logged as event 1260
  `PlatformTextsRejected` (category `Runic.Application.Views.Translations`) and the
  previous texts stay.

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
Set them from the catalog's `menu` messages in your `LocaleChanged` handler;
`{0}` stays for the application's name, which Runic inserts:

```csharp
static DesktopStandardMenuTitles StandardMenuTitles(RunicViewsTextMessages text) => new()
{
    About = text.menu_about(app: "{0}"), Hide = text.menu_hide(app: "{0}"), HideOthers = text.menu_hide_others,
    ShowAll = text.menu_show_all, Quit = text.menu_quit(app: "{0}"), File = text.menu_file, Close = text.menu_close,
    OpenRecent = text.menu_open_recent, ClearMenu = text.menu_clear_menu, Edit = text.menu_edit, Undo = text.menu_undo,
    Redo = text.menu_redo, Cut = text.menu_cut, Copy = text.menu_copy, Paste = text.menu_paste,
    SelectAll = text.menu_select_all, View = text.menu_view, ActualSize = text.menu_actual_size, ZoomIn = text.menu_zoom_in,
    ZoomOut = text.menu_zoom_out, Window = text.menu_window, Minimize = text.menu_minimize, Zoom = text.menu_zoom,
    BringAllToFront = text.menu_bring_all_to_front,
};

locale.LocaleChanged += (_, _) =>
    desktopHost.Desktop.SetStandardMenuTitles(StandardMenuTitles(provider.GetRequiredService<RunicViewsText>().Messages));
```

### Without dependency injection

```csharp
TranslationContext translations = TranslationContext.CreateBuilder()
    .Add(AppTextCatalog.Registration)
    .Add(RunicViewsTextCatalog.Registration)
    .Build("de");

var validation = new RunicValidationOptions { TextFor = RunicViewsTranslations.ValidationText };
using var platformTexts = RunicViewsTranslations.FollowPlatformTexts(translations);
```

`RunicViewsTranslations.PlatformTexts(context)` returns the texts without setting
them. With a scoped context (`options.Lifetime = ServiceLifetime.Scoped`), call
`FollowPlatformTexts` with the context to follow: `DesktopPlatformTexts` is
process-wide.

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
[Ship translations in a library](https://github.com/Runic-Artifex/runic-translations-sdk/blob/main/docs/guides/translations/libraries.md#the-application)
and the [consumer fixture](../../../tests/fixtures/application/views-translations-consumer/README.md),
which CI builds against the packed package.

## Keys

Keys and their inputs are API: an override names them, and the build checks it.
A message whose meaning changes gets a new key; removed keys are listed in the
release notes.

| Group | Keys | Used by |
| --- | --- | --- |
| `validation` | `required`, `max_length`, `min_length`, `length_between`, `range_integer`, `range_number`, `range`, `email`, `phone`, `url`, `credit_card`, `pattern`, `compare`, `file_extensions`, `base64`, `allowed_values`, `denied_values`, `enum` | `ValidationText` (input `field`, and `min`, `max`, `other`, `extensions`) |
| `field` | `required.number`, `required.whole_number`, `required.date`, `required.time`, `required.date_time`, `not_a_number`, `not_a_whole_number`, `too_many_fraction_digits`, `out_of_range`, `not_a_boolean`, `not_a_choice`, `not_a_date`, `not_a_date_in`, `not_a_time`, `not_a_time_in`, `not_a_date_time`, `offset_out_of_range`, `no_text_form` | the page's field errors (input `text`, and `scale`, `choices`, `patterns`) |
| `platform` | one per `DesktopPlatformTexts` text, such as `untitled_file_name`, `save_file_title`, `control_key`, `access_denied_title` | `PlatformTexts`, `FollowPlatformTexts` |
| `menu` | one per `DesktopStandardMenuTitles` title, such as `about`, `quit`, `edit`, `select_all` (input `app` for `about`, `hide`, `quit`) | the standard macOS menus |

The English text is the English that Runic shows without the package.

## Compatibility and status

This package targets .NET 10 and is versioned with the Runic SDK. Licensed under
the [MIT License](../../../LICENSE).
