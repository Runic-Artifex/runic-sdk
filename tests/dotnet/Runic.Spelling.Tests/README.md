# Runic Spelling tests

TUnit tests of Runic.Spelling:

- `DictionaryChoiceTests`: the dictionary a BCP 47 tag is checked with, and the precedence of the application's,
  downloaded and system dictionaries;
- `SystemFolderTests`: the system's dictionary folders on Linux, macOS and Windows, with fake folders and environment
  variables;
- `BundledDictionaryTests`: the English dictionaries the package's build targets copy into this suite's output, against
  their pinned hashes, and a few words read from each;
- `CheckerTests`: the checker's words, rules, skip ranges, known and user's words and suggestions;
- `DownloadTests`: downloads from a local HTTP server: an installed dictionary, a hash mismatch and a cancelled download
  that leave nothing installed, and the shipped catalogue's pins.

The cases share no state but the process-wide cache of read dictionaries, and run in parallel. Each works in a
temporary folder of its own and never reads the computer's dictionaries or the network.
