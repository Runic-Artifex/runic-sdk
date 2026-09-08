# Quality gates

The required pull-request check is the final `verify` job in
`.github/workflows/ci.yml`. It succeeds only when all build, managed, web,
engineering, framework-consumer, bridge, editor, documentation, customer,
package-consumer, template, native and footprint jobs succeed.

Run the Linux portion of the same workflow locally with `bun run ci`. Use
`--job <id>` to select a job and its prerequisites. The root `test` and `verify`
commands are aliases for this runner, not separate verification scripts.

Linux x64, Windows x64 and macOS Apple Silicon have native checks. A local Linux
pass does not certify Windows or macOS behavior. See the
[local CI guide](../../../../eng/ci/README.md) for setup and platform limits.

Build outputs carry a source digest and toolchain identity; downstream jobs check
both before reuse. Source and lockfile checks compare each job's final files with
its starting files, allowing developers to verify uncommitted work. Candidate
packages are tested outside the source workspace and are never published by CI.

Independent jobs can be rerun after a transient failure without repeating unrelated
successful suites. A GitHub rerun uses its original commit; pushed fixes need a new
run. Acceptance receipts apply only to their recorded source and artifact hashes.
