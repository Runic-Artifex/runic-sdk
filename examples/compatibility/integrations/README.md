# Integration canaries

These small projects consume published packages only. They make repository
boundaries testable while the larger historical samples are migrated away from
source-level project references.

- `RunicTextResources.Canary` restores the runtime, build integration, and source
  generator from GitHub Packages, generates a typed accessor, and executes it.
- `RunicCommandLine.Canary` restores all four Command Line packages from GitHub
  Packages, exercises their protocol, catalog, hosting, and process surfaces, and
  runs both managed and NativeAOT builds.
- `RunicFlow.Canary` restores all three Flow packages from GitHub Packages,
  exercises the framework-neutral operation kernel, generator contracts, and
  CommunityToolkit projection adapter, and runs both managed and NativeAOT builds.
- `RunicAssets.Canary` restores the core, CsWebUi, and ASP.NET Core packages,
  round-trips the neutral archive, verifies both host adapters, and runs managed
  and NativeAOT builds.
