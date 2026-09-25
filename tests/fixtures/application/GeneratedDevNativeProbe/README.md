# Generated native replacement fixture

This is internal test evidence for one managed `dotnet watch` restart of a
generated Window Bridge host. It has no public Window, View, Vite, or host API.

The private nested discovery project produces an owner-scoped ready manifest.
The random session owner isolates it from other builds. `dotnet runic dev`
owns the validated owner-root cleanup after its host, Vite server, and watcher
have stopped; the browser test does not assume a physical owner-root layout.
The native host compares that manifest with the adapter fingerprint compiled
into its assembly. If they differ, it stops before acknowledging readiness.
It then starts CS-WebUI and atomically writes a
descriptor containing its process ID, instance ID, fingerprint, private URL,
and the evaluated ready-manifest path. It writes the host-ready fingerprint
first, so the fixture Vite adapter only accepts descriptors whose three values
agree.

The browser fixture proves a title read/write through the generated attachment,
a frontend-only Vite HMR update, exactly one restart after a compiled discovery
change, one fresh CS-WebUI document, and HMR after the replacement. The Vite
adapter and its reconnect endpoint are private to this fixture. The restart
does not preserve UI state, bridge credentials, endpoints, or in-flight work.

## Focused Linux NativeAOT check

From the SDK root, publish this fixture with a fresh private owner, then run
the published executable through its fixture-only Chromium route check:

```sh
owner=$(uuidgen | tr -d '-' | tr 'A-F' 'a-f')
direnv exec . dotnet publish tests/fixtures/application/GeneratedDevNativeProbe/GeneratedDevNativeProbe.csproj \
  -c Release -r linux-x64 -p:PublishAot=true \
  -p:RunicPostMvvmDiscoveryBuildOwner="$owner" \
  -p:RunicPostMvvmDiscoveryOwnerDriver=true \
  -p:RunicApplicationFrontendEnabled=false
direnv exec . node tests/fixtures/application/GeneratedDevNativeProbe/published-aot-browser-smoke.mjs \
  "obj/pmd/ordinary/$owner/dependencies/GeneratedDevNativeProbe/bin/Release/net10.0/linux-x64/publish/Runic.Application.CsWebUi.Tests" \
  "obj/pmd/ordinary/$owner/generated/Release/net10.0/view-bridge.ready.json"
```

The check starts a fresh AOT host and Chromium, then verifies the source-generated
fixture and mount responses and a generated title read/write. Its Vite origin is
deliberately unavailable: this focused check does not test HMR or managed
restart. The development browser test covers those behaviors with a managed
host. The publish output lives under the exact owner root shown above; remove
that task-owned directory after collecting any needed diagnostics.
