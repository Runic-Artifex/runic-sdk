- Every tracked file has a component, or is engineering-only, neutral or a listed
  shared input; a planner test fails on any other. A new CI component owns the
  planner, the gates and the local CI tooling, so a change to them runs only the
  always-run jobs instead of every job and leg, and the CI scripts of platform
  steps plan only their legs.
- `eng/ci/flakes.mjs` reports the failures of recent `ci.yml` runs by test, grouped
  across runs: those seen twice without an issue, known flakes and the merge queue
  entries to re-queue (the flake policy, D069).
