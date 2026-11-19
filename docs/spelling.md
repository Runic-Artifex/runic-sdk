# Spell checking in an application

A Runic application can check spelling in two ways.

- **The webview's checker.** Runic Desktop turns it on and sets its languages for the whole application
  ([Runic.Desktop, Spell checking](../packages/dotnet/Runic.Desktop/README.md#spell-checking)). It is enough for
  fields in the user's own language. It cannot check one field in German and another in English: the checkers of
  WebKitGTK and macOS belong to the process, and WebView2 cannot be told the languages at all.
- **The application's own checker**, [Runic.Spelling](../packages/dotnet/Runic.Spelling/README.md) (preview). The
  application checks each text in the language it is written in, leaves out what is not prose, such as placeholders,
  markup or mentions, and draws the marks itself. A translation editor or a writing tool needs this.

## Using the application's own checker

1. Turn the webview's checker off where the application checks: `DesktopWindowOptions.SpellChecking = false` for the
   window, or `spellcheck="false"` on the field, so the two do not both mark words.
2. Find the dictionary of the text's language and load a checker:

   ```csharp
   var dictionaries = new SpellingDictionaries(new() { UserLanguages = DesktopLanguages.Preferred });
   var userWords = await SpellingUserWords.OpenAsync();
   if (dictionaries.Find("de-CH") is { } dictionary)
   {
       var checker = await SpellChecker.LoadAsync(dictionary, new() { KnownWords = projectWords, UserWords = userWords });
       IReadOnlyList<Misspelling> marks = checker.Check(text, skipRanges);
   }
   ```

   `Check` returns UTF-16 offsets into the text, the same offsets a JavaScript string has, so the page can draw the
   marks without converting them. Pass the ranges not to check: a translation editor passes each MessageFormat 2
   placeholder and markup element; a writing tool its mentions and comments.
3. Offer a missing dictionary, never download it unasked. `GetAvailability(tag)` says whether a language is checked
   with a bundled, downloaded or system dictionary, can be downloaded (with its size) or cannot be checked:

   ```csharp
   var availability = dictionaries.GetAvailability(DesktopLanguages.Preferred[0]);
   if (availability.Status == SpellingAvailabilityStatus.Downloadable)
       // "German (Switzerland): not installed, download 4.4 MB" with a button that calls:
       await dictionaries.DownloadAsync(availability.Download!, progress, cancellationToken);
   ```

   A download is shared by every Runic application of the user, so a language downloaded in one is checked in all.
4. Offer "Add to dictionary" with `userWords.AddAsync(word)`, and suggestions with `checker.Suggest(word)`.

English (United States and United Kingdom) ships with the package. The system's Hunspell dictionaries are used where
they are installed: on Linux those of the distribution's `hunspell-*` packages, and in a Nix-built application those of
`buildRunicApplication`'s `spellingDictionaries` ([Nix, Spell checking](../nix/README.md#spell-checking)).
The package README lists the folders on each system, the order in which the sources count, the catalogue of
downloadable dictionaries and their licences.

## Later

The operating systems' own checkers (the Windows Spell Checking API, macOS's `NSSpellChecker`) as sources of
Runic.Spelling are later work.
