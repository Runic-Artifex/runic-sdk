# Contract registry

| Contract | Identity | Owner |
| --- | --- | --- |
| MVVM wire protocol | `runic.toolkit.mvvm/1` | `RunicToolkit.MVVM` |
| Frontend asset manifest | `runic-toolkit.frontend-assets/1` | `RunicToolkit.Hosting.Build` |
| Frontend SDK artifact | `runic-toolkit.frontend/1` | `RunicToolkit.Frontend.Sdk` |
| External compiler diagnostics | `runic-toolkit.frontend-compiler.diagnostics/1.0` | Frontend SDK integration seam |
| External compiler hot reload | `runic-toolkit.frontend-compiler.hot-reload/1.0` | Frontend SDK integration seam |
| Rendered fragment inspection | `runic-toolkit.frontend-compiler.rendered-fragments/1.0` | Developer CLI |
| MVVM diagnostics | `RTKMVVM0001`–`RTKMVVM9999` | MVVM build/compiler |
| Hosting diagnostics | `RTKHOST0001`–`RTKHOST9999` | Hosting |
| Frontend SDK diagnostics | `RTKFE0001`–`RTKFE9999` | Frontend SDK |
| Developer CLI diagnostics | `RTKDEV1000`–`RTKDEV1999` | `dotnet-runic-toolkit` |

Integration-owned protocols and diagnostics are registered in the repository
that owns the integration. This registry must not reserve ranges for independent
products.
