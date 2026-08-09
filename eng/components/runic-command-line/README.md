![Runic Command Line banner](.github/assets/brand/banner.png)

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
A manual prerelease workflow can create a uniquely versioned, checksummed package
artifact. Publishing that verified artifact to the organization-scoped GitHub
Packages feed is a separate explicit choice and is disabled by default.
Changes to the packaging configuration exercise the same artifact path in pull
requests, but the publishing job cannot run for pull-request events.

## Development

Enter the Nix development shell and run the complete verification pipeline:

```bash
nix develop
./eng/verify.sh
```

Verification builds the standalone solution, runs all product tests, publishes and
executes a NativeAOT smoke application, then packs and consumes the four owned
packages from an isolated local NuGet feed.

To create the same package set locally after verification, supply a SemVer version
and an empty output directory:

```bash
./eng/pack.sh 0.1.0-preview.local ./artifacts/packages
```

Toolkit-specific launch and lifecycle mapping will live in a future
`RunicCommandLine.RunicToolkit` integration package owned by this repository.

## License

Runic Command Line is licensed under the [MIT License](LICENSE).
