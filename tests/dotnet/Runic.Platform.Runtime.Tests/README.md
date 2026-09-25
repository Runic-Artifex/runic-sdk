# Runic.Platform.Runtime.Tests

This non-packable executable verifies the host-neutral `Runic.Platform.Runtime`
contracts and the Windows, Linux, and macOS provider integrations. It has no
dependency on the Runic Application Views API.

Run the deterministic conformance suite from the locked development environment:

```sh
direnv exec . dotnet run --project tests/dotnet/Runic.Platform.Runtime.Tests/Runic.Platform.Runtime.Tests.csproj -c Release -p:TreatWarningsAsErrors=true
```

The suite covers owner admission and dispatch, clipboard outcomes, cancellation,
file access grants, leases, staged writes, conflict detection, and cleanup.
It does not open native UI during the ordinary managed or NativeAOT run.

## Interactive native checks

Use a real desktop session for these opt-in modes. The host opens a `Runic.Desktop`
embedded window and wraps that window's dispatcher as the platform owner.

```sh
# Exercise file handoff and notification APIs. Choose/dismiss native pickers as prompted.
direnv exec . dotnet run --project tests/dotnet/Runic.Platform.Runtime.Tests/Runic.Platform.Runtime.Tests.csproj -c Release -- --native-services

# Select an accessible file. The test reads at most 4096 bytes and prints no file content.
direnv exec . dotnet run --project tests/dotnet/Runic.Platform.Runtime.Tests/Runic.Platform.Runtime.Tests.csproj -c Release -- --native-select
```

`RUNIC_TEST_SERVICE` can select `All`, `Notifications`, `Open`,
`ChooseApplication`, or `Reveal` for `--native-services`. The macOS entry point
starts `DesktopEventLoop.Run` synchronously before opening its first WebView.
The sandboxed selected-file fixture is documented in
[`platform-sandbox`](../../native/platform-sandbox/README.md); signing and
selection are manual checks, not an automatic release gate.
