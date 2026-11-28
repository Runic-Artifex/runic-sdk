# Runic.Spelling

An application's own spell checker: Hunspell dictionaries and a checker that marks the misspelled words of a text in the
language it is written in, leaving out the ranges the application names, such as placeholders and markup.

> **Preview.** Runic.Spelling ships on a prerelease version of its own (`0.24.0-preview.1`) and is outside the 1.0
> compatibility promise, as `Runic.Application.Views.CsWebUi` is: its API may change in any release.

A webview's spell checker cannot do this for an application. On WebKitGTK and macOS it belongs to the process and checks
every field in the same languages, and WebView2 cannot be told the languages at all, so a German text is underlined with
an English dictionary. An application that checks its texts itself turns the webview's checker off for those fields
(`spellcheck="false"`) and draws the marks itself.

```sh
dotnet add package Runic.Spelling --prerelease
```

```csharp
using Runic.Spelling;

var dictionaries = new SpellingDictionaries(new SpellingDictionariesOptions
{
    UserLanguages = Runic.Platform.DesktopLanguages.Preferred, // optional: the region of a tag without one
});
var userWords = await SpellingUserWords.OpenAsync(); // shared by every Runic application of the user

var availability = dictionaries.GetAvailability("de-CH");
if (availability.Installed is { } dictionary)
{
    var checker = await SpellChecker.LoadAsync(dictionary, new SpellCheckerOptions
    {
        KnownWords = ["Runic", "Artifex"], // the application's or project's own words
        UserWords = userWords,
    });
    foreach (var word in checker.Check("Grüezi {$name}, das Wörterbuch ist da.", [new SpellingRange(7, 7)]))
        Console.WriteLine($"{word.Word} at {word.Start}: {string.Join(", ", checker.Suggest(word.Word))}");
}
else if (availability.Download is { } download)
{
    // Only when the user asks: "German (Switzerland): not installed, download 4.4 MB".
    await dictionaries.DownloadAsync(download, new Progress<SpellingDownloadProgress>(p => Console.WriteLine($"{p.BytesReceived}/{p.TotalBytes}")));
}
```

## The checker

`SpellChecker` checks with one dictionary, the words the application knows and the user's own words.

- `Check(text, skip)` returns each misspelled word with its start and length as UTF-16 offsets into `text`. `skip` lists
  ranges not to check, in any order: a Translations editor passes MessageFormat 2 placeholders and markup, a writing
  app its mentions and comments. A word is never taken across a range.
- `Suggest(word)` returns up to eight suggestions: known and user's words close to it first, then the dictionary's.
- Words are runs of letters with apostrophes, straight or curly, and hyphens between letters. A hyphenated compound is
  right when each part is, and each wrong part is marked on its own.
- Not checked: single letters, words with digits, words of up to six letters all in capitals (NATO, HTML), web and mail
  addresses.
- Known words count as spelled right: one written with a capital must be written so, one in lower case may also start a
  sentence, and either may be written all in capitals or with `'s`.
- `SpellingUserWords` is the user's own word list: a UTF-8 file of one word per line, by default `user-words.txt` in the
  directory all Runic applications share (below), or a file the application names. `AddAsync` and `RemoveAsync` keep the
  words another application added; a checker sees a change at once. Only the user can read the file: each write replaces
  it with a new file of mode 0600 on Linux and macOS, or with an access list of the user alone on Windows, and a
  directory created for it is 0700.

A checker is safe to use from several threads. Each dictionary is read once and shared by every checker of it. A
dictionary whose `.aff` names a code page in its `SET` line, such as ISO8859-15, is read with
`CodePagesEncodingProvider`, which the package registers when one does.

## Where dictionaries come from

| Source | What | Directories |
| --- | --- | --- |
| Bundled | The application's own: the package's English dictionaries, which its build targets copy into the build and publish output, and `SpellingDictionariesOptions.ApplicationDirectories` | `RUNIC_SPELLING_DICTIONARIES`, then `dictionaries/` beside the program (on macOS also the bundle's `Contents/Resources/dictionaries`, where `dotnet runic package macos` puts it) |
| Downloaded | Dictionaries of the catalog, downloaded when the user asks, shared by every Runic application of the user | Linux: `$XDG_DATA_HOME/runic/dictionaries` (`~/.local/share/runic/dictionaries`); Windows: `%LOCALAPPDATA%\Runic\dictionaries`; macOS: `~/Library/Application Support/Runic/dictionaries`; or `SpellingDictionariesOptions.DownloadDirectory` |
| System | Hunspell dictionaries already on the computer (`UseSystemDictionaries = false` leaves them out) | Linux: `$DICPATH`; `hunspell/`, `myspell/` and `myspell/dicts/` under `$XDG_DATA_HOME` and each `$XDG_DATA_DIRS` entry; `/usr/share/hunspell`, `/usr/share/myspell`. macOS: `~/Library/Spelling`, `/Library/Spelling`. Windows: each language's directory under `%APPDATA%\Microsoft\Spelling` |

On NixOS the profiles are on `$XDG_DATA_DIRS`, and an application built with Runic's Nix builder finds the dictionaries
its `spellingDictionaries` names there too.

**Precedence.** When several sources have a dictionary of the same name, the first wins: the application's own (with
`RUNIC_SPELLING_DICTIONARIES` before the others), then downloaded, then the system's. The application's dictionaries are
the ones it was tested with, so they check the same on every computer; a downloaded one is pinned by hash and was chosen
by the user; the system's vary in version from computer to computer. A dictionary that matches the language better wins
over one of an earlier source that matches worse: `de-CH` takes the system's `de_CH` over a downloaded `de`.

**Choosing a dictionary for a tag.** `SpellingDictionaries.Find` takes the first that is installed of: the tag's region
(`de_CH` for `de-CH`); its neighbour (Australian, Irish, New Zealand, South African and Indian English read with
British, Canadian with American, Austrian, Luxembourg and Belgian German with Germany's, Liechtenstein's with
Switzerland's); for a tag without a region, the region of each of the user's languages that is this language; the
language's usual region (`en_US`, `de_DE`); the language alone (`de`); then any other of the language's. A language is
never checked with another language's dictionary, and a script stays apart (`sr-Latn` never reads with Cyrillic `sr`).
Runic Desktop names the platform checkers' dictionaries from the same code, more strictly: a region there never reads
with another region's dictionary.

**Availability.** `GetAvailability(tag)` says what checks a language now (`Bundled`, `Downloaded`, `System`), or that a
dictionary can be downloaded (`Downloadable`, with `DownloadSize`) or that none exists (`Unavailable`). `Download` also
names a catalog dictionary that would serve the language better than the installed one, such as `de_CH` when only
`de_DE` is installed. An application offers the dictionary of the user's language with one click; the package never
downloads by itself.

## Downloads

The catalog (`SpellingCatalog.Default`) lists 90 Hunspell dictionaries, each pinned to a commit of its source: every
file by its `raw.githubusercontent.com` address at that commit, with its SHA-256 and size, and each dictionary's license
(`SpellingCatalogEntry.License`, an SPDX expression). `eng/spelling/generate-catalog.mjs` in the Runic SDK repository
generates it and checks every file against the commit's Git tree.

- [wooorm/dictionaries](https://github.com/wooorm/dictionaries) for every language but German: it names every
  dictionary by its BCP 47 tag, has them all in UTF-8, and gives each its readme, license text and license name.
- [LibreOffice/dictionaries](https://github.com/LibreOffice/dictionaries) for German (`de-DE`, `de-AT`, `de-CH`, the
  frami dictionaries from igerman98): wooorm's `de-CH` is not Swiss spelling (it has "Straße" and lacks "Strasse").

Dictionaries whose tag has a variant (`ca-valencia`, `el-polyton`) are left out: a Hunspell name has no place for a
variant.

`DownloadAsync(entry, progress, cancellationToken)`:

- requests only the catalog's HTTPS addresses, without redirects, with a generic `User-Agent` and nothing about the
  user or the application;
- writes the files to a directory of its own in the download directory, checks each one's size and SHA-256, and only then
  moves the directory into place in one step, so a mismatch, a failed request or a cancellation installs nothing and leaves
  nothing half-installed;
- keeps the dictionary's license texts (`LICENSE`, or `COPYING_GPLv2` and `COPYING_GPLv3`), its readme (source and
  license) and a `source.json` (the catalog entry) beside its `.dic` and `.aff`;
- reports the bytes received to `progress`.

A downloaded dictionary counts as installed, and `SpellChecker.LoadAsync` reads it, only while its `.dic` and `.aff` have
the size and SHA-256 they were installed with: the catalog's, or for an install of another catalog pin, which an
application with another release of this package can put in the shared directory, those its `source.json` records.
Otherwise `GetAvailability` offers it for download again, and loading it throws `InvalidDataException`. The size is
compared at each look, and the SHA-256 once per version of each file in the process, about 1 ms for a dictionary of the
median size and 18 ms for the largest of the catalog (38 MB).

## Licences

- **English dictionaries** (bundled): SCOWL 2026.02.25, `en_US` and `en_GB` (SCOWL's `en_GB-large`), under SCOWL's
  permissive license and the BSD license of Geoff Kuenning's affix file. `README_en_US.txt` and `README_en_GB.txt` go
  with them into the application.
- **Downloaded dictionaries**: each under its own license, named in the catalog (`SpellingCatalogEntry.License`,
  an SPDX expression) and kept beside it with its license texts and readme. Many are under the GPL or LGPL: they are data the
  application reads at run time, downloaded by the user, not part of the application.
- **WeCantSpell.Hunspell** 7.0.1, the checker, a port of Hunspell to .NET under Hunspell's tri-license (MPL 1.1, GPL
  2.0 or LGPL 2.1), used unchanged under the MPL 1.1. `LICENSE_WeCantSpell.Hunspell.txt` goes with the dictionaries
  into the application.

## Build

`RunicSpellingBundledDictionaries=false` leaves the English dictionaries out of the build and publish output, for an
application that ships its own or checks only languages the user downloads.

The package is trimmable and NativeAOT-compatible: the checker is managed code and the catalog is read without
reflection.

## Later

The operating systems' own checkers (the Windows Spell Checking API, macOS's `NSSpellChecker`) as further sources are
later work; until then those platforms use Hunspell dictionaries like Linux.
