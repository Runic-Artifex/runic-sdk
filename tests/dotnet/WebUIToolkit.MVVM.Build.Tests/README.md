# WebUIToolkit.MVVM.Build executable contracts

This project is a console test harness for the binding compiler and its build
integration. Its generated-member proof has one locked CommunityToolkit.Mvvm
package dependency. Run it from the repository root:

```console
dotnet restore tests/WebUIToolkit.MVVM.Build.Tests/WebUIToolkit.MVVM.Build.Tests.csproj --locked-mode -p:WebUIToolkitLocalPackageSource=<packed-feed>
dotnet run --project tests/WebUIToolkit.MVVM.Build.Tests/WebUIToolkit.MVVM.Build.Tests.csproj --configuration Release --no-restore
```

The packed feed must contain `WebUIToolkit.MVVM` version `1.0.0`; repository
orchestration creates it from the protocol/runtime owner before this project is
restored. The test project never uses a cross-owner project reference.

Every contract prints one `PASS` or `FAIL` line. The final line is stable and
machine readable: `TOTAL <total> PASSED <passed> FAILED <failed>`.

The committed NuGet lock is intentionally RID-free. Native-AOT smoke publishing,
when needed, must use an ignored intermediate lock rather than changing this file.

The executable also contains the Wave C generated-member proof fixtures
`communitytoolkit.generated-member.title.v1` and
`communitytoolkit.generated-member.submit-command.v1`. It compiles an attributed
CommunityToolkit.Mvvm 8.4.2 producer, reads its emitted PE metadata without loading
it, emits a direct-access adapter, then compiles and runs a separate consumer. The
consumer can only satisfy the fixture by setting/getting `Title` and invoking
`SubmitCommand` through that adapter.

The proof uses compiler API
`WebUIToolkit.MVVM.Build.Symbols.GeneratedMemberContractCompiler`, package
`CommunityToolkit.Mvvm` `8.4.2`, and the reserved diagnostics
`WUTMVVM2014` through `WUTMVVM2018`. Its Flow handoff is deliberately declarative:

| CommunityToolkit proof fixture | Planned Flow projection fixture |
| --- | --- |
| `communitytoolkit.generated-member.title.v1` | `flow.projection.communitytoolkit.title.v1` |
| `communitytoolkit.generated-member.submit-command.v1` | `flow.projection.communitytoolkit.submit-command.v1` |

This test project does not load CommunityToolkit runtime code to inspect symbols;
the separate consumer loads it only to execute the emitted direct-access proof.

The post-generator semantic tests use CommunityToolkit.Mvvm 8.4.2 only as a real
source-generator producer. They cover generated validating observable properties,
typed relay commands, parameterless and typed async relay commands, cancellation,
running/cancellation state, and exact serializer-metadata requirements. The emitted
artifact is compiled and executed in a separate consumer.

Reproducibility coverage builds that producer in two clean roots with distinct
MVIDs, invariant and Turkish cultures, and reversed requirement/reference
enumerations. It requires byte-identical source and JSON artifacts plus normalized
diagnostics. Hostile identifiers, malformed Unicode, control characters, unsafe
type requests, missing capabilities, and unsupported semantic schema versions are
rejected without source emission.
