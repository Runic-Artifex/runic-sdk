# Contract and identifier registry

This file is owned by the orchestrator. Domain tasks reserve identifiers here before making fixtures or diagnostics stable. ADR 0005 defines the migration from draft plan IDs.

| Family | Owner | Initial identity | Status |
|---|---|---|---|
| MVVM protocol | mvvm-protocol-core | `webuitoolkit.mvvm/1` | Reserved |
| CLI protocol | command-line | `webuitoolkit.cli/1` | Reserved |
| MVVM compiler diagnostics | mvvm-compiler-build | `WUTMVVM0001`–`WUTMVVM9999` | Reserved |
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
| template-engine | `WebUIToolkit.MVVM.Html`, `.Html.Testing` | NuGet pending |
| htmx | `WebUIToolkit.MVVM.Html.Htmx` | NuGet pending |
| hosting | `WebUIToolkit.Hosting.*` | NuGet pending |
| flow | `WebUIToolkit.MVVM.Flow*` | NuGet pending |
| text-resources | `WebUIToolkit.TextResources.*` | NuGet pending |
| command-line | `WebUIToolkit.CommandLine.*` | NuGet pending |
| collections | `WebUIToolkit.Collections`, `.Collections.Observable` | NuGet pending |
| dependency-notices | `WebUIToolkit.DependencyNotices.*` | NuGet pending |
| web-sdk-conformance | `@webuitoolkit/mvvm`, `@webuitoolkit/conformance` | npm scope pending |
| framework adapters | `@webuitoolkit/mvvm-angular`, `-react`, `-vue`, `-svelte` | npm scope pending |

## Runtime identity ranges

| Family | ActivitySource/Meter prefix | Structured event range |
|---|---|---|
| MVVM | `WebUIToolkit.MVVM` | `10000`–`10999` |
| Hosting | `WebUIToolkit.Hosting` | `11000`–`11999` |
| Flow | `WebUIToolkit.MVVM.Flow` | `12000`–`12999` |
| CommandLine | `WebUIToolkit.CommandLine` | `13000`–`13999` |
| TextResources | `WebUIToolkit.TextResources` | `14000`–`14999` |
| DependencyNotices | `WebUIToolkit.DependencyNotices` | `15000`–`15999` |

Schema `$id` values remain unset until an owned schema domain is available. File-format versions are independent from package versions.
