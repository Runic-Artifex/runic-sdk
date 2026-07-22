# Command-line parser evaluation (Wave A / C0)

This directory is an isolated, reproducible evaluation of parser candidates. It does **not** adopt a parser into a shipping project and does not modify the repository's central package policy. Candidate package references live only in inert templates copied to an operating-system temporary directory by `scripts/Invoke-CliEvaluation.ps1`; no lock file is generated or retained.

The stable first-party identities used by the fixture are `WebUIToolkit.CommandLine.*`, `WEBUITOOLKIT_CLI_OUTPUT`, and `webuitoolkit.cli/1`. Candidate-native types, help text, and diagnostics are evaluation details, never proposed public contracts.

## Reproduce

Prerequisites are the .NET SDK recorded in `candidate-manifest.json`, internet access to nuget.org, and the platform Native AOT toolchain. From the repository root:

```powershell
pwsh eng/cli-evaluation/scripts/Invoke-CliEvaluation.ps1
pwsh eng/cli-evaluation/scripts/Test-EvaluationArtifacts.ps1
```

The runner creates a fresh temporary root, copies each selected fixture, restores without a lock file, builds, attempts a Native AOT publish for the current RID, executes `corpus/grammar.json`, and emits a JSON result plus logs to a caller-selected output directory. Its default output is under the OS temporary directory, so reproduction does not dirty the worktree.

Use `-Candidate InHouse,SystemCommandLine` to run a subset and `-KeepWorkspace` to retain the expanded temporary projects for inspection. The checked-in `results/windows-x64-2026-07-22.md` records the executed Wave A probe and its limitations.

## Decision boundary

`ADR-candidate-parser.md` is a **candidate ADR**, not an accepted repository ADR. The scorecard selects `System.CommandLine` as the leading adapter candidate based on current primary evidence, but adoption is held because the authoritative root `Directory.Packages.props` has no approved parser version and this task is forbidden to edit it. The neutral C1 contracts can proceed without a package decision.
