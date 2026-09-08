# Current engineering acceptance

`bun eng/ci/test-acceptance.mjs` runs an explicit test list. Build the current
`tools/dotnet-runic` CLI first, using the repository development environment.

`size-command.test.mjs` invokes the actual CLI and publishes a temporary probe.
It verifies failed checks retain their measured evidence, reports cannot overwrite
existing evidence, and failed publish operations never execute verification.
Temporary projects and outputs are removed after the test.

Package consumers live in `eng/verify-packages.mjs` and `tests/templates/`.
Release candidate identity, archive hashes, exact source and mandatory evidence
are checked by the release tooling's tests; the retired historical receipt
schemas are not a source of current acceptance requirements.
