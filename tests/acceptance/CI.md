# Integrated candidate validation

The package-only workflows consume immutable packages from GitHub Packages.
The release authority's `runic.compatibility-set.json` is the single selection
of source revisions for an integration run. A revision maps to
`1.0.0-ci.sha<first-16-characters-of-revision>` for every package owned by that
repository.

## Advance the candidate set

Until coordination needs automation, advance the train manually:

1. Merge and verify candidate producers in dependency order:
   `runic-command-line`, `runic-desktop`, and `runic-vite`; then
   `runic-assets` and `runic-translations`; then `runic-toolkit`; then
   `runic-svelte`.
2. Confirm each selected main-branch workflow published and re-downloaded its
   exact GitHub Packages candidate.
3. Advance the corresponding full revision in the compatibility authority.
   Keep a producer on its prior known-good revision when it did not change.
4. Let the examples canaries and package-only samples consume that authority;
   they must
   restore from GitHub Packages; they must not clone or rebuild a producer.
5. Accept the authority change only after all Linux, NativeAOT, and Windows
   consumer jobs pass.

A missing package blocks the train. Do not substitute a source checkout or a
different package version. Public release promotion follows the same dependency
order and starts only after this integrated validation passes.

The dashboard and its scheduled data collector are deliberately deferred. The
central `.github` retention report remains the package-cleanup audit; automatic
candidate deletion stays disabled until the organization needs that additional
automation.
