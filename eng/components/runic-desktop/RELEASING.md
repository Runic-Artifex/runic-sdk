# Releasing Runic Desktop

Runic Desktop is currently a source preview and has no public package release.
Before the first preview, configure the `Runic.Desktop` NuGet trusted-publisher
identity for this repository and add a release workflow that verifies the tag
against the project package version.

A release candidate must pass the full operating-system build and test matrix,
the embedded WebView smoke on Windows, Linux, Intel macOS, and Arm macOS, and a
package-consumer restore from the exact candidate artifacts.

WebUI compatibility updates are adopted deliberately. Record the reviewed
upstream revision in the roadmap, run the behavioral suite, and document any
intentional divergence rather than treating upstream changes as automatic
merges.
