# Runic Command Line

Runic Command Line is a parser-neutral, NativeAOT-friendly command-line framework.
It owns portable contracts, command catalogs, execution, deterministic output,
host-neutral launch classification, and bounded child-process support without a UI
framework dependency.

This repository was extracted from Runic Toolkit with its product history intact.
Its clean-break product identity is `RunicCommandLine.*`; the retired Toolkit
package and namespace names are not compatibility aliases. Its portable machine
protocol is `runic.commandline/1`, diagnostics use `RCLI####`, and output-mode
selection can be supplied through `RUNIC_COMMANDLINE_OUTPUT`.

## Packages

| Package | Purpose |
| --- | --- |
| `RunicCommandLine.Abstractions` | Parser-neutral outcomes, diagnostics, console, and protocol contracts |
| `RunicCommandLine` | Command catalogs, portable parsing, execution, and deterministic output |
| `RunicCommandLine.Hosting` | Framework-neutral launch classification and execution handoff |
| `RunicCommandLine.Processes` | Bounded, cancellable child-process execution |

The packages are not published yet. The repository verifies their dependency
metadata and consumes them from an isolated local feed before any registry release.

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
