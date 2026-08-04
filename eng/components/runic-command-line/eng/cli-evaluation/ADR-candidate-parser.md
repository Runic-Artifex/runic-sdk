# Candidate ADR: Defer parser adoption; retain a neutral syntax boundary

- Status: Candidate / not accepted
- Date: 2026-07-22
- Scope: Wave A C0 evidence under `eng/cli-evaluation/**`

## Context

The command-line plan requires current stable evaluation of System.CommandLine, Spectre.Console.Cli, Cocona, CommandLineParser, and a minimal BCL-only control. A parser is adoptable only when it passes every mandatory Native AOT, portable grammar, public isolation, maintenance/license, help/localization, diagnostics/cancellation, and dependency rule and scores at least 80/100.

Repository policy adds a separate integration constraint: third-party package versions are centrally managed, and the authoritative root `Directory.Packages.props` contains no parser version. The command-line task cannot edit that shared policy. Evaluation package references therefore remain inert templates copied to temporary directories; they are not product references and do not generate retained lock files.

First-party identity is fixed as `RunicCommandLine.*`; machine output uses `RUNIC_COMMANDLINE_OUTPUT` and protocol identity `runic.commandline/1`. Parser-native types, messages, help documents, and error categories must not cross those boundaries.

## Candidate decision

**Do not adopt a parser package in Wave A. Retain the neutral `ICommandSyntaxAdapter` boundary and proceed with BCL-only contracts. Treat System.CommandLine 2.0.10 as the leading candidate for a later adapter, not as an approved dependency.**

System.CommandLine is the only mature candidate whose fixture built and published Native AOT with zero evaluated trim/AOT warnings. It scored 92/100, but native behavior differed in 3 of 33 portable corpus cases: invariant decimal conversion, duplicate Boolean options, and attached values on a flag. A later adapter must close those gaps and rerun the same corpus. Until that proof and a central package pin exist, the mandatory syntax and package-policy gates remain open.

The minimal in-house spike passed the narrow corpus and AOT publish. It is not selected because the plan requires explicit approval plus two independently shaped consumer scenarios before custom parsing, and neither is present. The control also lacks mature help/localization and compatibility evidence.

## Rejected candidates

- **Spectre.Console.Cli 0.55.0:** rejected for the mandatory AOT rule. The executed fixture fails with IL3050 at `CommandApp`; upstream explicitly marks the package non-AOT-compatible and non-trimmable because it uses reflection.
- **Cocona 2.2.0:** rejected for the mandatory AOT and maintenance rules. Native AOT publish fails with IL2104/IL3053, command discovery is reflection-driven, the package brings an older Microsoft.Extensions graph, and the repository is archived with no reactivation plan.
- **CommandLineParser 2.9.1:** rejected for mandatory AOT and syntax rules. Publish fails with IL2104, and true nested commands remain unsupported. Its last stable release was in 2022 and trimming work is unmerged.

## Consequences

- C1 abstractions, grammar corpus, outcomes, faults, exit categories, help model, and `runic.commandline/1` protocol identity remain library-owned and parser-neutral.
- No parser package or parser-native public type is introduced in Wave A.
- The orchestrator may later approve a central System.CommandLine version. That approval alone is insufficient; the internal adapter and packed consumer must pass the complete corpus and Native AOT gates.
- Native help/error prose is test evidence only. Stable diagnostics use owned `RCLI####` identities; this evaluation uses symbolic kinds except for the already established `RCLI1001` mapping.
- The in-house implementation cannot graduate from control to product parser without a separate decision demonstrating the required consumers and long-tail grammar/help burden.

## Evidence

The executable corpus, fixture templates, runner, normalized result, and weighted scorecard are all in this directory. Primary source links and package metadata are collected in `scorecard.md` and `candidate-manifest.json`. The executed Windows result is `results/windows-x64-2026-07-22.md`.
