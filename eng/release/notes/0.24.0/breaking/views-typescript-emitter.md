- **Building a project that generates Views clients needs Node.js or Bun.** The TypeScript clients are now written by the
  Views TypeScript emitter, `@runic-artifex/views/codegen`, which the build runs with Node.js or Bun
  (`RunicViewsGenerateClients`) after the .NET generator has written the C# Bridges and the contract IR
  (`obj/<configuration>/<framework>/runic-bridge/contract.ir.json`); the .NET generator writes no TypeScript any more. A
  project with `RunicApplicationFrontendGenerationEnabled=true` (the default with a host adapter) therefore needs `node`
  or `bun` on `PATH`, or `RunicApplicationFrontendCodegenRuntime` set to one, also when it builds no frontend; there is
  no .NET fallback. Without either, the build fails with
  [`RUNICBRIDGE084`](../../../docs/diagnostics.md#runicbridge084). The emitter is the one of the frontend's
  `@runic-artifex/views` when it is installed (resolved from `RunicApplicationFrontendDirectory`), which must then be
  0.24 or later, and the build installs the frontend's dependencies before it generates the clients (previously before
  the frontend build). Otherwise the build uses the copy of the emitter that `Runic.Application.Views` bundles, which
  matches the generator's version: when the project has no frontend (no `package.json` in
  `RunicApplicationFrontendDirectory`), such as a library or a test project, when the frontend's `package.json` declares
  no `@runic-artifex/views`, and when the build does not build the frontend (`RunicApplicationFrontendBuildEnabled=false`,
  as the `runic-app` template's test project sets). A frontend that declares `@runic-artifex/views` but does not have it
  installed after the install step (the install is disabled or failed) is RUNICBRIDGE084. The generated files are
  unchanged. A project with generation off, or without Views, needs neither.
