# Integration canaries

These small projects consume published packages only. They make repository
boundaries testable while the larger historical samples are migrated away from
source-level project references.

- `RunicTextResources.Canary` restores the runtime, build integration, and source
  generator from GitHub Packages, generates a typed accessor, and executes it.
- `RunicCommandLine.Canary` restores all four Command Line packages from GitHub
  Packages, exercises their protocol, catalog, hosting, and process surfaces, and
  runs both managed and NativeAOT builds.
- `RunicFlow.Canary` restores all four Flow packages from GitHub Packages,
  exercises the framework-neutral operation kernel, generator contracts, and
  CommunityToolkit plus Runic Toolkit adapters, and runs both managed and
  NativeAOT builds.
- `RunicAssets.Canary` restores the core, CsWebUi, ASP.NET Core, and Runic Toolkit
  packages, round-trips the neutral archive, verifies all three host adapters,
  and runs managed and NativeAOT builds.
- `RunicMarkup.Canary` restores the core language plus every published,
  Markup-owned Runic Toolkit integration package and exercises managed and
  NativeAOT consumption from GitHub Packages.
