## Publish from a web bundle, without Node.js, checked against the build's Views

`-p:RunicApplicationFrontendPrebuiltDirectory=<dir>` makes a build take each project's page from a web bundle that
another job wrote (D082), for example a Windows release job from the bundle of a Linux job. The build runs no frontend
install, no package or frontend build and no TypeScript client emission, so the runner needs neither Node.js nor Bun,
also with this release's emitter, which otherwise runs on Node.js or Bun in every build that generates Bridges. A WPF
application's model project takes its own entry, and `RunicApplicationFrontendDelivery=Embedded` packs the bundle's
page into the assembly. The property is also read from the environment, so `dotnet runic package` can use it.

Before it uses a page, the build compares the bundle's View fingerprints with the contract IR that its generator has
just written. A View that changed, or that only one side has, is `RUNICBRIDGE094`, which names the View and both
fingerprints; a page built for another host is `RUNICBRIDGE095`. Both are errors; `RunicApplicationFrontendPrebuiltCheck=warning`
publishes anyway with a warning. A missing entry or a page other than the bundle recorded, as after a truncated
download, is `RUNICBRIDGE093`, and an optimized build that takes a development page warns with `RUNICBRIDGE096`. See
[Building the frontend in another job](../../../packages/dotnet/Runic.Application.Views/README.md#building-the-frontend-in-another-job).
