# Tests

The solution contains executable contract-test projects rather than a single
test framework runner. `eng/run-contract-tests.ps1` discovers and runs the
managed suite after one solution build.

NativeAOT, real-browser, package-consumer, and template-acceptance projects are
separate gates because they require additional runtime or published-package
inputs. `tests/RunicToolkit.PackageCanary` is the release workflow’s isolated
NuGet consumer.
