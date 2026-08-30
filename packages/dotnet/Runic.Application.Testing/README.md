# Runic.Application.Testing

`Runic.Application.Testing` runs the same generated application manifest through
a headless host. Its clocks, IDs, environment, bridge, assets, and lifecycle
events are deterministic in memory, so tests do not rely on windows or sleeps.
Configure explicit `ApplicationCapabilityStatus` values when a test needs a
manifest-declared capability to be available; undeclared host configuration is
never projected, and unconfigured manifest capabilities are unavailable.

Create one `DeterministicApplicationTestHost` with explicit `initialTime`,
`idSeed`, and environment pairs. Its `Clock` and `Timers` advance only when the
test asks; `Ids`, `Environment`, `Bridge`, and `Assets` are in-memory bounded
fakes. Build the application with `RunicApplication.CreateBuilder(...).UseHost`
and pass `application.Manifest` to any fault, cancellation, or controlled-stop
case so every lifecycle path consumes the generated manifest rather than a
handwritten substitute.
