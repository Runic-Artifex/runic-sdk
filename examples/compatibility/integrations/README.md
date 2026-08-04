# Integration canaries

These small projects consume published packages only. They make repository
boundaries testable while the larger historical samples are migrated away from
source-level project references.

- `RunicTextResources.Canary` restores the runtime, build integration, and source
  generator from GitHub Packages, generates a typed accessor, and executes it.
