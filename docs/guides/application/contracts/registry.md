# Contract registry

| Contract | Identity | Owner |
| --- | --- | --- |
| Application Bridge envelope | application-defined identity and positive version | `RunicToolkit.ApplicationBridge` |
| Canonical bridge manifest | generator format `1` | `RunicToolkit.ApplicationBridge.Generators` |
| Frontend asset manifest | `runic-toolkit.frontend-assets/1` | `RunicToolkit.Hosting.Build` |
| External compiler diagnostics | `runic-toolkit.frontend-compiler.diagnostics/1.0` | Frontend SDK integration seam |
| External compiler hot reload | `runic-toolkit.frontend-compiler.hot-reload/1.0` | Frontend SDK integration seam |
| Rendered fragment inspection | `runic-toolkit.frontend-compiler.rendered-fragments/1.0` | Developer CLI |
| Application Bridge diagnostics | `RTKAB0001`–`RTKAB9999` | Application Bridge generator |
| Hosting diagnostics | `RTKHOST0001`–`RTKHOST9999` | Hosting |
| Developer CLI diagnostics | `RTKDEV1000`–`RTKDEV1999` | `dotnet-runic-toolkit` |

Integration-owned protocols and diagnostics are registered in the repository
that owns the integration. This registry must not reserve ranges for independent
products.
