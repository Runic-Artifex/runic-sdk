# Releasing Runic Desktop

Runic Desktop is currently a source preview and has no public package release.
Before the first preview, configure the `Runic.Desktop` NuGet trusted-publisher
identity for this repository and add a release workflow that verifies the tag
against the project package version.

A release candidate must pass the full operating-system build and test matrix,
the embedded WebView smoke on Windows, Linux, Intel macOS, and Arm macOS, and a
package-consumer restore from the exact candidate artifacts.

The local package gate deliberately verifies the SHA-512 retained by NuGet's
clean extraction and checks that the extracted package still contains the
runtime assembly, README, NOTICE, WebUI attribution, and consumer build target.
It also runs a NativeAOT package consumer. This is unsigned local certification,
not a public distribution claim: a later publication decision must separately
approve NuGet trusted publishing, Windows signing, macOS signing/notarization,
and any platform-specific distribution metadata.

Runic Desktop currently publishes libraries, not an application-owned
self-contained desktop archive. A future application bundle must add its own
archive inspection and smoke evidence for application assets, per-user state,
startup preflight, crash recovery, signing, and notarization; those claims must
not be inferred from the library package gate.

Run the repository-owned checks with:

```console
npm run verify:contract
nix develop --command bash eng/verify-local-package.sh linux-x64
```

The Linux gate is routine. Windows and macOS WebView smokes run only for an
explicit manual dispatch or a `v*` release-candidate tag, keeping expensive
native capacity out of ordinary pushes and pull requests.

WebUI compatibility updates are adopted deliberately. Record the reviewed
upstream revision in the roadmap, run the behavioral suite, and document any
intentional divergence rather than treating upstream changes as automatic
merges.
