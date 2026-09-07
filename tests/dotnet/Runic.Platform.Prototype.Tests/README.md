# Internal OS-service prototype

This non-packable executable contains internal contract and lifetime experiments
for the [OS integration RFC](../../../docs/guides/application/architecture/os-integration-rfc.md).
Nothing here is a shipped native provider or public `Runic.Platform` API.

Run from the SDK root in `nix develop`:

```sh
dotnet run --project tests/dotnet/Runic.Platform.Prototype.Tests -c Release -p:TreatWarningsAsErrors=true
```

Both SDK solutions include the executable, so the existing root test runner also
runs it. The prototype files depend only on the BCL. References to Application,
Desktop and CS-WebUI are used by the composition tests, not by the contracts.

## Evidence

The controlled backend, callback queue and access lease exercise nine grouped
scenarios without sleep-based race coordination:

- Missing or temporarily unavailable providers, required ownership, explicit
  unowned selection, immutable readiness snapshots and presentation generations.
- Pre-cancelled calls, dismissal, one open/save picker per owner across facades,
  independent owners and admission recovery after provider exceptions.
- Caller cancellation and owner shutdown with a late selected lease: wait for
  release, release once, reject subsequent work, and preserve cleanup failures.
- Worker-to-owner dispatch, inline nested dispatch, queued cancellation, callback
  exceptions, queued shutdown rejection and draining already-running callbacks.
- Identical feature DI registrations beside real Desktop and CS-WebUI host
  compositions. Headless service resolution returns explicit unavailable results;
  disposing one scope leaves another independent scope usable.

The composition test builds the real host objects and validated service graphs.
It **does not start either host**, bind a native window owner, invoke a native
dispatcher, or prove bridge-session scope ownership. `AttachTestOwner` is only a
simulated state transition, never evidence of a valid HWND, NSWindow or portal
parent token. A host must retain a logical presentation scope across reconnect
and close it when replacing its owner; that wiring remains to be implemented.

## Contract refinements and remaining work

Picker dismissal has its own closed `PickerResult<T>` family, so clipboard
results cannot accidentally express dismissal. Successful selection cannot carry
a null lease. `PlatformResult<T>` still permits a successful null clipboard read.
Save commit outcomes distinguish committed, not committed and unknown.

Only the picker facade and managed lifetime/dispatch rules are implemented.
Save transaction and clipboard types are contract sketches. File filters, title
and native filename validation, concrete read leases and stream ownership,
atomic writes, clipboard bounds, composition capability projection and actual
host-owned service scopes remain part of the next implementation slices.
Successful returned leases transfer to the caller; only late rejected selections
are released by this facade. Native providers must separately implement and prove
lease/stream lifetime and SDK-acquired access release.

Do not promote the contracts to published packages or label a platform supported
based on these fake-provider tests. Native execution, package-only consumers,
NativeAOT and provider footprint comparisons remain separate gates.
