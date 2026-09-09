# MAUI-derived text document migration

This comparison extracts the portable document orchestration of a MAUI page into
`Before/DocumentViewModel.cs`. `DocumentPage.xaml.reference` illustrates its binding
surface; it is not a compiled MAUI application and does not establish MAUI/native
acceptance. CommunityToolkit remains solely in the before fixture.

`Domain/DocumentService` is shared unchanged. It accepts strict UTF-8 text up to 32
KiB, rejects NUL, reads through scoped leases, and saves through required atomic
replacement. This bound leaves room for JSON escaping within the standard bridge
frame limits. A save captures its text before the picker opens. Dismissal,
cancellation, unavailability, failures and uncertain commits remain distinct.
An uncertain commit must be inspected by the user before any retry. Cleanup
failures are reported separately and never replace an acknowledged or uncertain
write outcome.

The Runic application exposes two cancellable commands and an authoritative
operation snapshot. React owns the text draft, edit revision, and dirty-close
policy. Operation results carry the revision captured by the command. A late open
never replaces a newer edit; a completed save marks only the captured text saved.
Snapshot generations prevent reconnect or receipt/event ordering from replaying
an open. No MVVM adapter, path, stream, owner handle or lease crosses the bridge.

## Run from the SDK checkout

Use the locked development environment, with root dependencies installed by the
workspace tooling. From the repository root:

```sh
direnv exec . dotnet run --project examples/document-migration/Tests/DocumentMigration.Tests.csproj
direnv exec . bun test examples/document-migration/Host/Frontend/src/editor-state.test.ts
direnv exec . dotnet run --project examples/document-migration/Host/DocumentDesktop.csproj -- --native
```

`--native` selects embedded Desktop with verified ownership and a scoped static
provider. `RunicNativeProvider` defaults to the build OS; explicitly choose
`Windows`, `Linux` or `MacOS` for a cross-target build. Only that provider is
referenced. Build with `-p:RunicHost=cswebui` to use CS-WebUI, whose shared platform
services explicitly report unavailable. `--serve` runs the bridge without opening
a window; it cannot satisfy native-dialog acceptance.

Open is disabled for a dirty draft. Save it first, or choose New and confirm
discard. The editor remains usable during operations. Cancel requests cancellation;
a write already committing reports its actual outcome. Embedded native close uses
an accessible confirmation dialog and rejects close while work is pending. Browser
close uses `beforeunload`. The saved document survives process restart; open that
file again to restore its content. Unsaved drafts are presentation-local.

## Exact candidate consumption outside the checkout

```sh
python examples/document-migration/eng/export-candidate.py /tmp/runic-document-candidate \
  --version 0.2.0-preview.1 --nuget-feed /absolute/path/to/verified/nuget \
  --npm-feed /absolute/path/to/verified/npm
cd /tmp/runic-document-candidate
dotnet tool restore --configfile NuGet.config
cd Host/Frontend
bun install --ignore-scripts
cd ../..
dotnet run --project Host/DocumentDesktop.csproj -- --native
```

The npm feed is the local directory of immutable `.tgz` files. Metadata must match
the exact candidate version. The exporter validates the internal dependency closure
and binds both direct dependencies and transitive overrides to those archives.
Missing versions, mixed versions, duplicate identities and workspace/ranged internal
dependencies fail before creating a consumer. Omit `--npm-feed` only after the exact
version exists in the public registry. NuGet source mapping keeps SDK packages and
the local `dotnet-runic` tool on the candidate feed.

Run the complete bounded package-only check from the SDK's locked environment:

```sh
python examples/document-migration/eng/test-candidate.py
python examples/document-migration/eng/verify-candidate.py \
  --version 0.2.0-preview.1 --nuget-feed /absolute/path/to/verified/nuget \
  --npm-feed /absolute/path/to/verified/npm \
  --report /absolute/path/to/document-package-receipt
```

The check creates an external consumer, installs exact npm archives and the packaged
.NET tool, runs the portable comparison, then builds and browser-tests Desktop with
no provider, Desktop with the current OS provider, and CS-WebUI. It checks resolved
NuGet graphs for exact SDK versions, omitted-provider isolation and absence of
Desktop/ASP.NET Core dependencies in CS-WebUI. Source inspector overrides are
removed. Each build/install has a ten-minute deadline; each browser run has two
minutes. Temporary consumers/caches are removed, while logs, graphs, artifact hashes,
source revision and outcomes remain in the new report directory. Existing reports
are never overwritten. Linux execution uses the caller's configured browser/display
from the development environment; choose `GDK_BACKEND=x11` for the headless X11 case.

The exporter replaces Runic project references with exact package references,
removes checkout imports, and relies on packaged generators/build tooling.
It refuses to overwrite an existing candidate directory. It does not install MAUI
workloads, publish artifacts, or claim tests passed. Record failures with the package version, OS and reproduction steps.

## Manual checks

Use a brief native session when changing dialogs, clipboard, focus or window
lifecycle in this example. Record affected scenarios and any bugs in the PR or
issue. Routine releases do not require repeating the demos or collecting
acceptance receipts. See the [current release policy](../../eng/release/README.md).
