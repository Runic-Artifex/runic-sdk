## Runic host protocol conformance through the driver

The conformance driver runs the [Runic host protocol scenarios](../../../specs/desktop/host-conformance/README.md) as
the corpus `host/1`: for the guest role it plays the host over a real loopback WebSocket, and for the host role the
guest, through a relay guest and a channel listener of its own. It drives the implementation under test through a small
adapter, so a guest or host in any language is checked by the same steps. `bun run conformance dotnet-desktop` runs
Runic's .NET guest and host, which pass all 180 scenarios. The scenarios now name the seams a runner needs, how an
outcome is derived from an implementation's features, and the page-transport profile that the region scenarios' frame
and component requests use after their upgrade.
