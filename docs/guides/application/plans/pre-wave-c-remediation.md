# Pre-Wave-C remediation brief

Status: Current
Wave C: Not started
Execution policy: ADR 0010 and the workflow operating manifest

## Goal

Close the two remaining readiness gaps without starting Wave C:

1. prove that the two-stage MVVM compiler observes actual
   CommunityToolkit.Mvvm-generated property and command members;
2. record the Flow fixture mapping and dependency handoff needed by the later
   projection work.

Use a small one-off TypeScript workflow. Bun owns the authoritative restores,
tests, architecture checks, namespace checks, Git diff, telemetry, and fix-loop
inputs. Agents implement, diagnose, review semantics, and synthesize. Use normal
trusted-local package and network access.

Do not recreate the legacy approved Plan, plan hash, preparation commit, command
catalog, isolated package cache, or immutable baseline chain.

## Stage 1: compiler foundation

The compiler must inspect compiled PE metadata without `Assembly.Load`, runtime
reflection, generated-source inspection, or `obj` probing.

Keep the reserved public contract:

```csharp
namespace WebUIToolkit.MVVM.Build.Symbols;

public static class GeneratedMemberContractCompiler
{
    public static GeneratedMemberContractResult Compile(
        GeneratedMemberContractRequest request);
}

public sealed record GeneratedMemberContractRequest(
    string AssemblyPath,
    string MetadataTypeName,
    IReadOnlyList<GeneratedMemberRequirement> Members);

public sealed record GeneratedMemberRequirement(
    string BindingMemberId,
    string GeneratedMemberName,
    GeneratedMemberKind Kind,
    string ExpectedTypeMetadataName);

public enum GeneratedMemberKind
{
    Property = 0,
    Command = 1,
}

public sealed record GeneratedMemberContractResult(
    IReadOnlyList<WebUIToolkit.MVVM.Build.Generation.GeneratedBindingArtifacts> Artifacts,
    IReadOnlyList<WebUIToolkit.MVVM.Build.Compiler.BindingDiagnostic> Diagnostics);
```

Keep the reserved diagnostics:

| ID | Meaning |
| --- | --- |
| `WUTMVVM2014` | Assembly not found |
| `WUTMVVM2015` | Type not found |
| `WUTMVVM2016` | Member missing |
| `WUTMVVM2017` | Member inaccessible or incompatible |
| `WUTMVVM2018` | Member ambiguous or duplicate |

The blocked historical candidate exposed two correctness requirements that must be
implemented and tested:

- validate or safely encode metadata-derived generated C# identifiers and type
  syntax; reject unsupported values with a stable diagnostic and cover hostile
  identifiers;
- use a standards-compliant JSON serializer/writer, or complete JSON escaping for
  control characters, and cover hostile/control-character values.

Bun must run:

```powershell
dotnet restore --locked-mode tests/WebUIToolkit.MVVM.Build.Tests/WebUIToolkit.MVVM.Build.Tests.csproj
dotnet run -c Release --no-restore --project tests/WebUIToolkit.MVVM.Build.Tests/WebUIToolkit.MVVM.Build.Tests.csproj
dotnet restore --locked-mode tests/WebUIToolkit.MVVM.BindingCompiler.Tests/WebUIToolkit.MVVM.BindingCompiler.Tests.csproj
dotnet run -c Release --no-restore --project tests/WebUIToolkit.MVVM.BindingCompiler.Tests/WebUIToolkit.MVVM.BindingCompiler.Tests.csproj
pwsh -NoProfile -File ./eng/verify-architecture.ps1
pwsh -NoProfile -File ./eng/verify-namespaces.ps1
```

## Stage 2: CommunityToolkit generated-member proof

Use CommunityToolkit.Mvvm `8.4.2`.

The producer declares:

- `partial class GeneratedMemberViewModel : ObservableObject`;
- `[ObservableProperty] private string? title`;
- `[RelayCommand] private void Submit()` incrementing
  `public int SubmissionCount`.

The generated identities under test are:

- `public string? Title`;
- `public IRelayCommand SubmitCommand`.

Stage 1 compiles the attributed producer to a PE. Stage 2 resolves those generated
members from that PE through the compiler foundation, emits direct-access adapter
C#, compiles a separate consumer, and runs it.

The consumer sets and gets `Title`, checks
`SubmitCommand.CanExecute(null)`, executes the command, and verifies that
`SubmissionCount` equals `1`. Direct ViewModel calls or dispatcher callbacks do not
satisfy the proof.

Determinism covers two clean temporary roots, invariant and non-default cultures,
and forward/reversed enumeration of the same metadata-reference set. Generated
bytes and normalized diagnostics must match and exclude absolute paths, timestamps,
MVIDs, locale, and machine state.

Reserved proof fixture IDs:

- `communitytoolkit.generated-member.title.v1`;
- `communitytoolkit.generated-member.submit-command.v1`.

## Stage 3: Flow handoff

After the compiler and CommunityToolkit proof pass, record the dependency versions,
compiler API identity, fixture identities, diagnostics, and a one-to-one mapping:

| CommunityToolkit proof fixture | Planned Flow projection fixture |
| --- | --- |
| `communitytoolkit.generated-member.title.v1` | `flow.projection.communitytoolkit.title.v1` |
| `communitytoolkit.generated-member.submit-command.v1` | `flow.projection.communitytoolkit.submit-command.v1` |

This stage validates the handoff only. It does not load CommunityToolkit runtime
code or implement Flow projection behavior.

## Completion

The one-off workflow leaves reviewed changes uncommitted in the invoking worktree.
After ordinary review and integration, run a narrow readiness assessment and tell
the user whether Wave C is ready. Do not start Wave C automatically.
