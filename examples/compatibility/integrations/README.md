# Integration canaries

These small projects consume published packages only. They make repository
boundaries testable while the larger historical samples are migrated away from
source-level project references.

- The Runic Translations canary (`RunicTranslations.Canary`, retaining the
  package identity) restores the runtime, build integration, and source generator
  from GitHub Packages, generates a typed accessor, and executes it.
- `RunicCommandLine.Canary` restores all four Command Line packages from GitHub
  Packages, exercises their protocol, catalog, hosting, and process surfaces, and
  runs both managed and NativeAOT builds.
- `RunicFlow.Canary` restores the two headless Flow packages from GitHub Packages,
  exercises serialized process decisions and the Application Bridge operation
  identity boundary, and runs both managed and NativeAOT builds.
- `RunicAssets.Canary` restores the core, CS-WebUI, ASP.NET Core, and Runic Toolkit
  packages, round-trips the neutral archive, verifies all three host adapters,
  and runs managed and NativeAOT builds.
