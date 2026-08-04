# Runic Command Line

Runic Command Line is a parser-neutral, NativeAOT-friendly command-line framework.
It owns portable contracts, command catalogs, execution, deterministic output,
host-neutral launch classification, and bounded child-process support without a UI
framework dependency.

This repository was extracted from Runic Toolkit with its product history intact.
The current `WebUIToolkit.CommandLine.*` identities are retained temporarily so the
standalone build can be proven before the clean-break `RunicCommandLine.*` rename.
No package is published under the retired identity.

## Development

Enter the Nix development shell and run the complete verification pipeline:

```bash
nix develop
./eng/verify.sh
```

Verification builds the standalone solution, runs all product tests, publishes and
executes a NativeAOT smoke application, then packs and consumes the four owned
packages from an isolated local NuGet feed.

Toolkit-specific launch and lifecycle mapping will live in a future
`RunicCommandLine.RunicToolkit` integration package owned by this repository.

## License

Runic Command Line is licensed under the [MIT License](LICENSE).
