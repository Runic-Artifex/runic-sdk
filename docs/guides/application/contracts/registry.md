# Contract and identifier registry

This file is owned by the orchestrator. Domain tasks reserve identifiers here before making fixtures or diagnostics stable. ADR 0005 defines the migration from draft plan IDs.

| Family | Owner | Initial identity | Status |
|---|---|---|---|
| MVVM protocol | mvvm-protocol-core | `webuitoolkit.mvvm/1` | Reserved |
| Release rehearsal | release-rehearsal | `webuitoolkit.release-rehearsal-matrix/1` | G7 allocated |
| CLI protocol | command-line | `webuitoolkit.cli/1` | Reserved |
| MVVM compiler diagnostics | mvvm-compiler-build | `WUTMVVM0001`–`WUTMVVM0003`, `WUTMVVM0901`–`WUTMVVM0903`, `WUTMVVM1001`–`WUTMVVM1006`, `WUTMVVM2001`–`WUTMVVM2013` | Allocated; remainder reserved |
| MVVM compiler diagnostic | mvvm-compiler-build | `WUTMVVM2014=AssemblyNotFound` | Allocated |
| MVVM compiler diagnostic | mvvm-compiler-build | `WUTMVVM2015=TypeNotFound` | Allocated |
| MVVM compiler diagnostic | mvvm-compiler-build | `WUTMVVM2016=MemberMissing` | Allocated |
| MVVM compiler diagnostic | mvvm-compiler-build | `WUTMVVM2017=MemberInaccessibleOrIncompatible` | Allocated |
| MVVM compiler diagnostic | mvvm-compiler-build | `WUTMVVM2018=MemberAmbiguousOrDuplicate` | Allocated |
| MVVM compiler diagnostic | mvvm-compiler-build | `WUTMVVM2019=PostGeneratorSemanticContractUnsupported` | Allocated |
| Remediation fixture | communitytoolkit | `communitytoolkit.generated-member.title.v1` | Reserved |
| Remediation fixture | communitytoolkit | `communitytoolkit.generated-member.submit-command.v1` | Reserved |
| G3 fixture | communitytoolkit | `communitytoolkit.observable-property.v1` | Allocated |
| G3 fixture | communitytoolkit | `communitytoolkit.relay-command.v1` | Allocated |
| G3 fixture | communitytoolkit | `communitytoolkit.async-command-cancellation.v1` | Allocated |
| G3 fixture | communitytoolkit | `communitytoolkit.validation-metadata.v1` | Allocated |
| G3 fixture | communitytoolkit | `communitytoolkit.generated-metadata.v1` | Allocated |
| G3 projection | template-engine | `webuitoolkit.cwhtml.communitytoolkit/1` | Allocated |
| G3 projection | htmx | `webuitoolkit.mvvm.htmx/1` | Allocated |
| G3 projection | flow | `flow.projection.communitytoolkit.contract.v1` | Allocated |
| Remediation fixture | flow | `flow.projection.communitytoolkit.title.v1` | Reserved |
| Remediation fixture | flow | `flow.projection.communitytoolkit.submit-command.v1` | Reserved |
| G3 fixture | flow | `flow.projection.communitytoolkit.async-command.v1` | Allocated |
| Flow diagnostics | flow | `WUTFLOW0001`–`WUTFLOW0999` | ADR 0005 reserved |
| Template diagnostics | template-engine | `WUTHTML0001`–`WUTHTML7999` | ADR 0005 reserved |
| Text diagnostics | text-resources | `WUTTEXT0001`–`WUTTEXT0999` | ADR 0005 reserved |
| CLI diagnostics | command-line | `WUTCLI0001`–`WUTCLI9999` | ADR 0005 reserved |
| Notice diagnostics | dependency-notices | `WUTNOTICE1000`–`WUTNOTICE7999` | ADR 0005 reserved |
| Hosting diagnostics | hosting | `WUTHOST0001`–`WUTHOST0007`, `WUTHOST1001`, `WUTHOST1101`–`WUTHOST1103`, `WUTHOST1201`–`WUTHOST1202`, `WUTHOST1301`, `WUTHOST1401`–`WUTHOST1405` | Allocated; remainder reserved by ADR 0005 |

## Package identities

| Owner | Reserved IDs | External reservation |
|---|---|---|
| mvvm-protocol-core | `WebUIToolkit.MVVM` | NuGet pending |
| communitytoolkit | `WebUIToolkit.MVVM.CommunityToolkit` | NuGet pending |
| template-engine | `WebUIToolkit.MVVM.Html`, `WebUIToolkit.MVVM.Html.Testing`, `WebUIToolkit.MVVM.Html.CommunityToolkit` | NuGet pending |
| htmx | `WebUIToolkit.MVVM.Html.Htmx`, `WebUIToolkit.MVVM.Html.Htmx.Js` | NuGet pending |
| hosting | `WebUIToolkit.Hosting.*` | NuGet pending |
| flow | `WebUIToolkit.MVVM.Flow*` | NuGet pending |
| text-resources | `WebUIToolkit.TextResources.*` | NuGet pending |
| command-line | `WebUIToolkit.CommandLine.*` | NuGet pending |
| collections | `WebUIToolkit.Collections`, `.Collections.Observable` | NuGet pending |
| dependency-notices | `WebUIToolkit.DependencyNotices.*` | NuGet pending |
| web-sdk-conformance | `@webuitoolkit/mvvm`, `@webuitoolkit/conformance` | npm scope pending |
| Wave E framework adapters | `@webuitoolkit/mvvm-react`, `@webuitoolkit/mvvm-vue`, `@webuitoolkit/mvvm-svelte` | G5 technically approved; npm publication blocked by ADR 0004 |
| Wave F framework adapters | `@webuitoolkit/mvvm-angular`, `WebUIToolkit.MVVM.ReactiveUI` | G6 technically approved; publication blocked by ADR 0004 |
| Wave G release rehearsal | Ten NuGet packages plus `@webuitoolkit/mvvm` and `@webuitoolkit/mvvm-angular` | G7 technically approved as an isolated package set; publication blocked by ADR 0004 |

## Runtime identity ranges

| Family | ActivitySource/Meter prefix | Structured event range |
|---|---|---|
| MVVM | `WebUIToolkit.MVVM` | `10000`–`10999` |
| Hosting | `WebUIToolkit.Hosting` | `11000`–`11006` allocated; `11007`–`11999` reserved |
| Flow | `WebUIToolkit.MVVM.Flow` | `12000`–`12999` |
| CommandLine | `WebUIToolkit.CommandLine` | `13000`–`13999` |
| TextResources | `WebUIToolkit.TextResources` | `14000`–`14999` |
| DependencyNotices | `WebUIToolkit.DependencyNotices` | `15000`–`15999` |

Schema `$id` values remain unset until an owned schema domain is available. File-format versions are independent from package versions.
