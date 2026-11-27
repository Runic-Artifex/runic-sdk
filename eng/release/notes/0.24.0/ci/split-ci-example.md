- **The split CI example runs on every package pull request.** The package consumers job runs `examples/split-ci`
  against the packed packages (`examples/split-ci/package-smoke.mjs`): the tests and the web bundle, then a `win-x64`
  publish from the bundle with neither Node.js nor Bun on `PATH`, and a ViewModel change that `RUNICBRIDGE094` must
  stop. The engineering job lints the example's two workflow files, and `tests/engineering/split-ci-example.test.mjs`
  checks that they differ only in the artifact actions and the default runner labels.
