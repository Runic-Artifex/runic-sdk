- **Runic.Spelling: a downloaded dictionary is used only while its files are the ones it was installed with, and the
  user's word list is readable by the user alone.** A downloaded dictionary whose `.dic` or `.aff` no longer has the
  size and SHA-256 it was installed with (for example after a crash during the install) no longer counts as installed:
  `GetAvailability` offers it for download again, and `SpellChecker.LoadAsync` throws `InvalidDataException`. Each file's
  hash is computed once per version of the file in the process (about 1 ms for a dictionary of the median size, 18 ms
  for the largest). Downloads are written to the disk before they are moved into place. The personal word list
  (`SpellingUserWords`) is now written as a new file of mode 0600 on Linux and macOS, or with an access list of the
  current user alone on Windows, and moved into place in one step; the directories the package creates for it and for
  downloads are 0700.
