# Wave A parser candidate scorecard

Evaluation date: 2026-07-22. Scores use the weights and mandatory rules in `command-line.html`. A numeric score above 80 is insufficient when any mandatory rule fails. Executed measurements are from `results/win-x64-summary.json`; maintenance, license, dependency, and upstream compatibility claims use the primary sources linked below.

| Candidate | AOT / trim 25 | Syntax 20 | Isolation 15 | Maintenance / license 10 | Help / localization 10 | Diagnostics / cancellation 10 | Size / deps 10 | Total | Mandatory result |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| System.CommandLine 2.0.10 | 25 | 17 | 15 | 10 | 8 | 8 | 9 | **92** | **Not yet passed:** AOT passes, but 3/33 native grammar dispositions differ and no adapted fixture closes them. |
| Spectre.Console.Cli 0.55.0 | 0 | 17 | 6 | 9 | 9 | 5 | 3 | **49** | **Rejected:** explicit IL3050 / reflection AOT failure. |
| Cocona 2.2.0 | 0 | 10 | 5 | 3 | 8 | 3 | 2 | **31** | **Rejected:** IL2104/IL3053; archived upstream; reflection discovery. |
| CommandLineParser 2.9.1 | 0 | 10 | 7 | 4 | 7 | 5 | 7 | **40** | **Rejected:** IL2104 and no true nested commands. |
| Minimal in-house spike | 25 | 20 | 15 | 2 | 5 | 8 | 10 | **85** | **Not selectable:** lacks required approval and two independent consumer scenarios. |

## Reproducible observations

- `System.CommandLine` restored, built, and published Native AOT with no owned IL2026/IL3050/IL2104/IL3053 warning. Its native fixture matched 30/33 portable dispositions. G024 used the current process culture instead of invariant decimal conversion; G031 accepted a repeated Boolean flag; G033 accepted an attached Boolean value. All three require owned adapter behavior and regression tests before the syntax gate passes.
- `Spectre.Console.Cli` was stopped at managed build because AOT analyzers reported IL3050 at `new CommandApp()`. This is expected: upstream declares `IsAotCompatible=false`, `IsTrimmable=false`, and annotates the app for dynamic code.
- `Cocona` managed build succeeded, but Native AOT publish failed with IL2104 in Cocona/Cocona.Core and IL3053 in Cocona.Core and its Microsoft.Extensions 6 dependency graph. It matched 17/33 portable dispositions before publish.
- `CommandLineParser` managed build succeeded, but Native AOT publish failed with IL2104 in CommandLine.dll. It matched 19/33 dispositions, and the fixture could only emulate `cache clear` as a verb plus positional value.
- The in-house control passed all 33 disposition/path cases and Native AOT. It is smaller than the System.CommandLine fixture, but the evaluation does not cover the mature candidate's full help, completion, validation, accessibility, or long-tail parsing surface. Passing a deliberately narrow spike does not satisfy the custom-parser approval rule.

## Package and maintenance evidence

| Candidate | Stable package and dependency graph selected by net10.0 | Maintenance / license evidence |
|---|---|---|
| System.CommandLine 2.0.10 | net8.0 asset, no package dependencies; netstandard2.0 fallback depends on System.Memory. | MIT; stable published 2026-07-14; active 2.0 servicing and 3.0 previews. |
| Spectre.Console.Cli 0.55.0 | net10.0 asset; Spectre.Console, then Spectre.Console.Ansi. | MIT; stable published 2026-04-03; active repository. |
| Cocona 2.2.0 | net6.0 fallback; Cocona.Core plus Microsoft.Extensions.DependencyInjection, Hosting, and Logging 6.0. | MIT; stable published 2023-03-27; repository archived 2025-12-14 with no reactivation plan. |
| CommandLineParser 2.9.1 | netstandard2.0 asset; no package dependencies. | MIT license file; stable published 2022-05-17; no later stable release and trimming work remains unmerged. |
| In-house | BCL only. | First-party source remains under repository publication hold ADR 0004; no independent maintainer or compatibility history. |

Primary sources:

- System.CommandLine: [NuGet 2.0.10](https://www.nuget.org/packages/System.CommandLine/2.0.10), [official command-line documentation](https://learn.microsoft.com/dotnet/standard/commandline/), [project AOT properties](https://github.com/dotnet/command-line-api/blob/main/src/System.CommandLine/System.CommandLine.csproj), [MIT license](https://github.com/dotnet/command-line-api/blob/main/LICENSE.md).
- Spectre.Console.Cli: [NuGet 0.55.0](https://www.nuget.org/packages/Spectre.Console.Cli/0.55.0), [project AOT properties](https://github.com/spectreconsole/spectre.console.cli/blob/main/src/Spectre.Console.Cli/Spectre.Console.Cli.csproj), [`CommandApp` dynamic-code annotation](https://github.com/spectreconsole/spectre.console.cli/blob/main/src/Spectre.Console.Cli/CommandApp.cs), [MIT license](https://github.com/spectreconsole/spectre.console.cli/blob/main/LICENSE.md).
- Cocona: [NuGet 2.2.0](https://www.nuget.org/packages/Cocona/2.2.0), [archival statement](https://github.com/mayuki/Cocona/issues/178), [AOT failure](https://github.com/mayuki/Cocona/issues/133), [reflection-based command provider](https://github.com/mayuki/Cocona/blob/v2.2.0/src/Cocona.Core/Command/CoconaCommandProvider.cs), [MIT license](https://github.com/mayuki/Cocona/blob/v2.2.0/LICENSE).
- CommandLineParser: [NuGet 2.9.1](https://www.nuget.org/packages/CommandLineParser/2.9.1), [Native AOT failure](https://github.com/commandlineparser/commandline/issues/897), [open trimming work](https://github.com/commandlineparser/commandline/pull/913), [open nested-command request](https://github.com/commandlineparser/commandline/issues/353), [MIT license](https://github.com/commandlineparser/commandline/blob/master/License.md).

## Decision

Adopt no package in Wave A. `System.CommandLine` is the leading candidate for a later internal adapter, conditional on:

1. the orchestrator approving and centrally pinning its package version;
2. an adapter passing all 33 portable cases, including invariant conversion and duplicate/flag semantics;
3. packed-consumer AOT evidence under repository policy; and
4. parser-native messages and help remaining outside stable `WebUIToolkit.CommandLine.*` and `webuitoolkit.cli/1` contracts.

The minimal in-house spike remains a control only. Selecting it would require explicit approval and two independent consumer scenarios, neither of which exists in Wave A.
