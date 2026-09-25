# Internal View Bridge development handoff

This is an opt-in coordination seam for the experimental MSBuild-owned View
Bridge. It does not enable a View Bridge by itself. The application project
must publish a ready manifest containing a nonempty `fingerprint` string, and
its managed host must publish the fingerprint of the bridge it actually loaded.

The project exposes two evaluated MSBuild properties:

```xml
<!-- A normal non-discovery fixture can use a fixed app-owned ready path. -->
<RunicApplicationViewBridgeReadyManifest>$(MSBuildProjectDirectory)/obj/runic/view-bridge.ready.json</RunicApplicationViewBridgeReadyManifest>
<RunicApplicationViewBridgeHostReadyPath>$(MSBuildProjectDirectory)/obj/runic/view-bridge-host.fingerprint</RunicApplicationViewBridgeHostReadyPath>
```

Both properties are required together. They must point to different files.
This mode requires a Vite development server and cannot be combined with the
legacy Application Bridge contract. Default applications have neither
property and retain their current development behavior.

The post-MVVM discovery fixture is different: its generated ready manifest is
scoped by both its selection key and the `RunicPostMvvmDiscoveryBuildOwner`
created by the top-level development session. Its project publishes the
corresponding owner-scoped path as `RunicApplicationViewBridgeReadyManifest`;
the host-ready acknowledgement remains app-owned. A fixed example above must
not be copied into an owner-aware discovery fixture.

With frontend watching enabled, `dotnet runic dev` installs the declared
frontend dependencies, builds the managed project so MSBuild can generate the
View Bridge modules, and then starts Vite. It skips the premature production
frontend build for this opt-in path. After the managed build, it verifies the
ready manifest and removes any host-ready marker left by a previous
development session. It passes both absolute paths to Vite as
`RUNIC_VIEW_BRIDGE_READY_MANIFEST` and `RUNIC_VIEW_BRIDGE_HOST_READY`. The same
paths reach the managed host. The published Runic Vite plugin does not expose
this experimental seam: the test fixture owns a small adapter that watches the
descriptor, ready manifest, and host-ready marker.

The host writes `RUNIC_VIEW_BRIDGE_HOST_READY` atomically, as a text file with
the loaded fingerprint and a trailing newline, **after** constructing its
View Bridge. `dotnet runic dev` never writes that acknowledgment. The private
fixture adapter waits for the marker to match a newly published ready manifest
before one browser reload. Vite does not inspect C# sources for this mode.

## Remaining integration

The internal `GeneratedDevNativeProbe` now exercises this seam end to end:
the post-MVVM fixture emits its ready manifest, a CS-WebUI host acknowledges
the loaded fingerprint, and its fixture-local Vite adapter waits for a
validated replacement descriptor before it sends one private handoff event.
The replacement document fetches and acknowledges that descriptor before it
navigates. The browser probe also holds a test-only host-ready gate to prove
that a changed manifest alone cannot navigate the old document.

This remains fixture evidence. The generated adapter, title route, descriptor
endpoint, private Vite event, and native-document replacement policy are not
public authoring or Vite-plugin APIs. A restart still drops the old document's
state, credentials, endpoints, and in-flight work.

`dotnet runic dev` keeps `dotnet watch` as the only managed restart owner.
Opt-in projects disable managed Hot Reload because a post-generator contract
needs a full build before the replacement host can acknowledge it. The focused
fixture edits a compiled contract, observes the changed ready fingerprint,
verifies one replacement host, and confirms Vite only reloads after that host
acknowledges the exact value.
