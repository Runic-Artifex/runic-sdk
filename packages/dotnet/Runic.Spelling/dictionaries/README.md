# Runic.Spelling's bundled dictionaries

The English Hunspell dictionaries that ship in the Runic.Spelling package. The package's build targets copy every
file here but this README into an application's build and publish output as `dictionaries/`
(`RunicSpellingBundledDictionaries=false` turns that off); `dotnet runic package macos` moves the folder into the
bundle's `Contents/Resources`. They are the files Artificer's Storyline ships, pinned by hash.

| Dictionary | Language | Source | Licence |
| --- | --- | --- | --- |
| `en_US` | English (United States) | SCOWL's Hunspell dictionary 2026.02.25 | SCOWL's permissive licence, and the BSD licence of Geoff Kuenning's affix file: `README_en_US.txt` |
| `en_GB` | English (United Kingdom) | SCOWL's `en_GB-large` 2026.02.25, renamed | the same: `README_en_GB.txt` |

British English is SCOWL's large dictionary rather than its `en_GB-ise`, which keeps one spelling of each word and so
marks "among" and "learned" (it has "amongst" and "learnt"). The large one has both, and the Oxford spellings in -ize
beside the ones in -ise, as British publishers use either; it still marks "color" and "harbor".

`LICENSE_WeCantSpell.Hunspell.txt` is the licence of the checker that reads them, WeCantSpell.Hunspell 7.0.1, a port of
Hunspell to .NET (<https://github.com/aarondandy/WeCantSpell.Hunspell>). It is Hunspell's tri-licence, MPL 1.1,
GPL 2.0 or LGPL 2.1; Runic.Spelling uses the library unchanged under the MPL 1.1. It goes with the dictionaries so that
every application carries it.

## Pinned versions

| Archive | SHA-256 |
| --- | --- |
| <https://downloads.sourceforge.net/wordlist/speller/2026.02.25/hunspell-en_US-2026.02.25.zip> | `ac8e73310e951d88c52c2cf2ba54ceaca34f8486a81630ac8a75dc5f931179f9` |
| <https://downloads.sourceforge.net/wordlist/speller/2026.02.25/hunspell-en_GB-large-2026.02.25.zip> | `704a49438a3a9d4f11cd76db433a6b230b9ce8770ee13fa28700052c4517ddc1` |

| File | SHA-256 |
| --- | --- |
| `en_US.aff` | `526e1dc32d1c0e8806dd72809c017b5546f2104b08b9424a4c47311aca526cca` |
| `en_US.dic` | `ab012ae86ce4b46b60db5b150869288de9c4a87bc747911764e582125b1b961e` |
| `en_GB.aff` | `b9fb2eef4b64cf39243565579dcedb84747d7986be0a8048f428f60db1fa0f2e` |
| `en_GB.dic` | `4a174235eb36947496f495815b2a04019fd45818b035b84a2368bedc45bacf5d` |
| `README_en_US.txt` | `d63c711523fc9504fb71e1278489c6d7bb8cafcba75158c107cd17844955470f` |
| `README_en_GB.txt` | `dd2f9d4e0e2d7b20cdb4b3e8c8f6dfb2268e459a0d063e951e95b4b1986ca185` |
| `LICENSE_WeCantSpell.Hunspell.txt` | `6d69e66d4533c9b9048db18d3f340b077313dc5d33324066f1b91cfbca15be00` |

`BundledDictionaryTests` in `tests/dotnet/Runic.Spelling.Tests` checks the copies in its output folder against these
hashes, so a dictionary changes only on purpose.
