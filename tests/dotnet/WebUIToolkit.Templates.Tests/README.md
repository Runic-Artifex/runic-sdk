# WebUIToolkit template acceptance

The executable test packs `WebUIToolkit.Templates`, installs that exact
artifact into an isolated temporary template-engine invocation, creates all
five project variants, packs their transitive WebUIToolkit dependencies in an
isolated source copy, restores the generated projects only from that feed plus
NuGet.org, and completes all five production builds.

```console
dotnet run --project tests/WebUIToolkit.Templates.Tests
```

The gate rejects repository-relative project references, developer-machine
paths, sample harness switches, missing npm locks, and failed name transforms.
It also catches package-content, buildTransitive import-order, cwhtml
generation, and frontend production-build failures.
