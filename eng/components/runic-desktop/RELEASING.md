# Releasing CS-WebUI

CS-WebUI already publishes its two NuGet packages through the `NuGet Gallery`
workflow when a GitHub release is published. Its nuget.org trusted-publisher
policy is bound to `.github/workflows/nuget-gallery.yml` and the `nuget`
environment; keep that workflow identity until the registry policy is migrated
deliberately.

Before publishing a release, verify that the release tag exactly matches the
project package version, all package and NativeAOT checks pass, and the `nuget`
environment variable `NUGET_USER` names the nuget.org account. CS-WebUI can keep
releasing independently of the Runic Toolkit package family.

## Updating the reviewed WebUI source

NuGet packages contain source-built native artifacts from the exact
`Runic-Artifex/webui` revision and header SHA-256 in
`eng/webui-source-build-provenance.json`. The package workflow builds that
revision for every supported RID, records the SHA-256 of each packaged native
binary, and verifies the packed provenance before publishing artifacts.

To adopt a newer reviewed source revision deliberately:

1. Update the native source and obtain its reviewed commit ID.
2. Update `eng/abi/experimental-source.json`,
   `eng/webui-source-build-provenance.json`, and their header digest together.
3. Regenerate the experimental ABI bindings and manifest, then update the
   managed streaming surface only when required by the ABI change.
4. Run `nix flake check --accept-flake-config`, native CTest, the current and
   previous managed compatibility lanes, NativeAOT smoke, and package consumers.

The release workflow packages only source-built artifacts from that reviewed
revision. Do not substitute release archives or an unrelated native revision.
