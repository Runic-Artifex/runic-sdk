# Platform service architecture

`Runic.Platform` provides host-independent, typed contracts for native file
selection, staged saves, text clipboard access, UI dispatch and capability
availability. `Runic.Platform.Runtime` implements the presentation lifetime,
leases and provider infrastructure. Native providers are selected explicitly;
the contracts do not depend on Desktop, CS-WebUI or ASP.NET Core.

| Package | Responsibility |
| --- | --- |
| `Runic.Platform` | Public service contracts and typed results |
| `Runic.Platform.Runtime` | Presentation lifetime, leases and provider wrappers |
| `Runic.Platform.Windows`, `.Linux`, `.MacOS` | OS-specific picker and clipboard providers |
| `Runic.Application.Platform` | Presentation-scoped DI registration and shutdown participation |
| `Runic.Application.Platform.Desktop` | Verified Desktop window ownership and dispatch integration |

The public contracts deliberately separate picker dismissal (`PickerResult<T>`)
from operational success, unavailability and failure (`PlatformResult<T>`).
`IReadFileLease` and `ISaveFileLease` retain acquired access in C#; a save uses a
single staged transaction and reports a known or uncertain commit outcome. An
application must not expose native handles, paths, streams or leases in bridge
payloads.

`IPlatformCapabilities` returns an immutable snapshot for the current
presentation generation. A capability report is not a grant: operations recheck
their owner and provider state. Provider absence, a missing verified owner, owner
shutdown and backend limitations are explicit unavailable outcomes. Permission
denial, resource contention and user dismissal are operation results.

Register `AddRunicPlatform()` in the bridge presentation scope and supply a
`PlatformProvider` factory when native services are intended. The runtime drains
operations and leases through `IApplicationPresentationLifetime` before the host
destroys that presentation. Reconnection retains the logical scope; replacing a
presentation invalidates its old owner and pending native work.

Desktop integration requires a verified native owner. Browser-backed CS-WebUI
presentations can use the scoped contracts but do not infer a native dialog owner
from the process or bridge connection. Platform-specific composition, supported
operations and verification status are maintained in the
[desktop services guide](../../desktop-services.md). For the exact public API,
see [Runic.Platform](../../../../packages/dotnet/Runic.Platform/Contracts.cs) and
the provider package READMEs.
