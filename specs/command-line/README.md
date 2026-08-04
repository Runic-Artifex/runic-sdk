# Command-line conformance specification

This directory contains the Wave A, language-neutral command-line grammar and output-classification contract. It is independent of any parser package and can be consumed offline with a standard UTF-8 JSON parser.

- `grammar.md` defines token recognition, binding, diagnostics, and normalized results.
- `grammar-corpus.json` supplies a fixed neutral catalog and parser fixtures.
- `output-classification-corpus.json` supplies `--output` and `RUNIC_COMMANDLINE_OUTPUT` precedence fixtures.

The JSON `protocol` field is the registered machine protocol identity `runic.commandline/1`; `formatVersion` versions the corpus container independently. Arrays are ordered. Consumers must preserve argument strings exactly, including empty strings, Unicode scalar sequences, slash-prefixed paths, and duplicate values. No fixture requires shell parsing, network access, environment mutation, locale data, or an external service.

Parse each JSON file with comments and trailing commas disabled. A conforming adapter executes every fixture against the catalog in `grammar-corpus.json` and compares the normalized `expected` result defined in `grammar.md`.
