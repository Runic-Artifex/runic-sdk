# Verification

Run focused tests and relevant build/type checks for the behavior you changed.
Use `bun run test --list` to find checks and `bun run test <scope>` to run them.
`bun run verify <scope>` is an alias for the same focused runner. See the
[contribution guide](../../../../CONTRIBUTING.md) for examples.

GitHub owns the full CI workflow, including package and template consumers and
native checks on Linux x64, Windows x64 and macOS arm64. For workflow debugging,
`bun run ci --job <id>` runs a selected job and its prerequisites locally; see
[local CI](../../../../eng/ci/README.md).

Passing the relevant checks completes local verification unless new changes or
failures justify more work. Scope manual native/UI checks to behavior automation
cannot cover. Soaks and benchmarks are regression investigation tools, not routine
publication prerequisites. The [release policy](../../../../eng/release/README.md)
is authoritative; historical acceptance records do not add release gates.
