## Runic host protocol conformance through the driver

The conformance driver runs the [Runic host protocol scenarios](../../../specs/desktop/host-conformance/README.md) as
the corpus `host/1`: for the guest role it plays the host over a real loopback WebSocket and drives the guest under test
through a small adapter, so a guest in any language is checked by the same steps. `bun run conformance dotnet-desktop`
runs Runic's .NET guest, which passes all 103 guest scenarios. The scenarios now name the seams a runner needs, how an
outcome is derived from a guest's features, and the page-transport profile that the region scenarios' frame and
component requests use after their upgrade. The host role's scenarios still run through the .NET host runner.
