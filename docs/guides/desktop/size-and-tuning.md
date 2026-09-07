# Measure and tune a published application

`dotnet runic size` publishes into a new directory, retains the publish log and
complete ZIP, and records a JSON inventory with hashes and byte counts. It never
removes files based on its category guesses.

```sh
dotnet runic size --project App.csproj --runtime linux-x64 --host desktop \
  --profile default --report measurements/desktop.json
dotnet runic size --project App.csproj --runtime linux-x64 --host desktop \
  --profile minimal --report measurements/minimal.json
dotnet runic size --project App.csproj --runtime linux-x64 --host cswebui \
  --report measurements/cswebui.json
```

The command uses Release, self-contained NativeAOT and size optimization by
default. Use `--no-aot` for a managed comparison. It preserves your project's
culture and diagnostic settings. Each report needs a new path so previous
evidence is retained. `--output json` selects the command's JSON envelope;
`--report` selects the measurement file. Progress streams to stderr in JSON mode.

A successful publish alone has verification status `not-run`. Supply a checker
that exercises the actual published application to earn `passed`:

```sh
dotnet runic size --runtime linux-x64 --report measurements/checked.json \
  --verify bun --verify-argument ./check-published-app.mjs
```

The checker receives its explicit arguments, then the publish directory and
main executable path. Arguments are passed without a shell. A nonzero checker
exit records `failed` and makes the size command fail. Publish failures also
produce a report and retain diagnostics. The checker controls its application
lifetime; it should fail on crashes and forced termination. The SDK's customer
checker tests validation, save, cancel, reconnect and shutdown of the published
NativeAOT executable.

Reports include SDK, build OS/architecture, installed runtime versions, target
RID, package/project dependency versions, source revision when available,
evaluated MSBuild settings, every file's SHA-256, total disk bytes and actual ZIP
bytes. Empty MSBuild values mean unspecified, not false; some defaults are
computed later by publish targets. Each command stream is bounded to 4 Mi
characters and explicitly marked if truncated. The report is local and includes
paths and command arguments: review it before sharing.

## Minimal Desktop hosting

Enable the opt-in profile for ordinary publishing with:

```xml
<PropertyGroup>
  <RunicDesktopMinimalHost>true</RunicDesktopMinimalHost>
  <PublishAot>true</PublishAot>
  <OptimizationPreference>Size</OptimizationPreference>
</PropertyGroup>
```

The default host uses ASP.NET Core's slim builder. The minimal profile uses the
empty builder and registers Kestrel's core server and socket transport explicitly.
A linker feature switch removes the unused builder branch. Listener isolation,
surfaces, WebSockets, admission, origin and message limits, and shutdown use the
same code. Both profiles pass the 69 Desktop conformance tests locally.

The profile omits the slim builder's default configuration providers and other
optional hosting defaults. Applications relying on host configuration or implicit
services must test those assumptions; `ConfigureServices` still runs. It does
not add MVC, authentication, HTTPS configuration or other ASP.NET facilities.
It does not remove any documented Runic presentation or bridge capability.
The default profile remains available by omitting the property or setting false.
This property has no role in CS-WebUI, which has no ASP.NET Core dependency.

## Choose tradeoffs using evidence

- NativeAOT and `OptimizationPreference=Size` are useful starting points. Keep
  trimming and AOT warnings visible and test the published executable.
- `InvariantGlobalization=true` changes culture behavior. Only choose it when
  the application's formatting, parsing and translations support that decision.
- Stack traces and diagnostics have support value. Do not disable them globally
  to improve a headline number. Check evaluated switches before varying them;
  NativeAOT already disables some features.
- Keep symbols separately if your deployment needs crash analysis. Count the
  complete distribution as well as the executable and required native libraries.
- Documentation and other-RID loaders can inflate a publish directory. The size
  report identifies candidates for packaging review; retaining or excluding them
  does not change the linked executable. Do not remove the loader for a native
  presentation mode your application uses.
- Embedded frontend assets are included inside their executable or assembly.
  An assembly list cannot attribute NativeAOT bytes. Startup time and process-tree
  memory need separate measurements; neither is inferred from file size.

The [verified Linux x64 package-consumer measurement](../../../eng/host-footprint/linux-x64-2026-09-07/README.md)
records the same customer editor under all three profiles, with exact settings,
package hashes, file inventories and successful browser/lifecycle checks. Minimal
Desktop reduced its executable by about 9.6%. These application-specific results
are separate from the older minimal sample's 8.25 MiB Desktop measurement.

## Reproduce the package comparison

After building and packing local package candidates:

```sh
nix develop
CONFIGURATION=Release bun run pack
CONFIGURATION=Release bun run verify:footprint
```

The matrix creates isolated consumers outside the checkout. Every SDK reference
comes from candidate NuGet packages, while the customer's own application and
domain projects remain local. All candidates embed the same compiled frontend.
The fixture explicitly chooses invariant globalization and stripped symbols; this
is an experiment setting, not a template default. Results, logs, hashes and ZIPs
are retained under `artifacts/host-footprint/<RID>/run-*/`. Browser and OS libraries
are excluded from the byte counts. The matrix records the source revision and
whether the checkout was dirty.

The CI matrix targets Windows x64, Linux x64, macOS x64 and macOS arm64. A configured
job is not evidence of a passing platform: inspect its uploaded reports. Local
measurements here are NixOS builds run in the repository shell; their native linker
and OS-library paths must not be treated as a portable distribution for arbitrary
Linux machines. Use the target platform's packaging and runtime environment.
