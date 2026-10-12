# Runic.Platform.Runtime.Tests

This non-packable TUnit test application (Microsoft.Testing.Platform) verifies the host-neutral
`Runic.Platform.Runtime` contracts and the Windows, Linux, and macOS provider integrations. It has no
dependency on the Runic Application Views API.

Run the deterministic conformance suite from the locked development environment. The explicit
native cases below are skipped unless a filter selects them:

```sh
direnv exec . dotnet run --project tests/dotnet/Runic.Platform.Runtime.Tests/Runic.Platform.Runtime.Tests.csproj -c Release -p:TreatWarningsAsErrors=true
```

The suite covers owner admission and dispatch, clipboard outcomes, cancellation,
file access grants, leases, staged writes, conflict detection, and cleanup.
It does not open native UI during the ordinary managed or NativeAOT run.

## Interactive native checks

Use a real desktop session for these opt-in cases, each an explicit TUnit case selected by its
category. The host opens a `Runic.Desktop` embedded window and wraps that window's dispatcher as
the platform owner. Run one category per process.

```sh
# Exercise file handoff and notification APIs. Choose/dismiss native pickers as prompted.
direnv exec . dotnet run --project tests/dotnet/Runic.Platform.Runtime.Tests/Runic.Platform.Runtime.Tests.csproj -c Release -- --treenode-filter "/*/*/*/*[Category=NativeServices]"

# Select an accessible file. The test reads at most 4096 bytes and prints no file content.
direnv exec . dotnet run --project tests/dotnet/Runic.Platform.Runtime.Tests/Runic.Platform.Runtime.Tests.csproj -c Release -- --treenode-filter "/*/*/*/*[Category=NativeSelect]"
```

`RUNIC_TEST_SERVICE` can select `All`, `Notifications`, `Open`,
`ChooseApplication`, or `Reveal` for the `NativeServices` category. The entry point is
hand-written for macOS: when a native category is selected it runs the test application inside
`DesktopEventLoop.Run`, which owns the AppKit loop on the process main thread. It can use the
generated entry point once the macOS cases no longer need that loop on the main thread.

The sandboxed selected-file fixture is documented in
[`platform-sandbox`](../../native/platform-sandbox/README.md); signing and
selection are manual checks, not an automatic release gate.
