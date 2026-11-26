- **Building a project that generates Views clients needs Node.js or Bun.** The TypeScript clients are now written by the
  Views TypeScript emitter, `@runic-artifex/views/codegen`, which the build runs with Node.js or Bun
  (`RunicViewsGenerateClients`) after the .NET generator has written the C# Bridges and the contract IR
  (`obj/<configuration>/<framework>/runic-bridge/contract.ir.json`); the .NET generator writes no TypeScript any more. A
  project with `RunicApplicationFrontendGenerationEnabled=true` (the default with a host adapter) therefore needs `node`
  or `bun` on `PATH`, or `RunicApplicationFrontendCodegenRuntime` set to one, also when it builds no frontend; there is
  no .NET fallback. Without either, the build fails with
  [`RUNICBRIDGE084`](../../../docs/diagnostics.md#runicbridge084). The emitter is the one of the frontend's
  `@runic-artifex/views`, resolved from `RunicApplicationFrontendDirectory`, so the frontend must have
  `@runic-artifex/views` 0.24 or later installed, and the build installs the frontend's dependencies before it generates
  the clients (previously before the frontend build). The copy of the emitter that `Runic.Application.Views` bundles is
  used only when the project has no frontend (no `package.json` in `RunicApplicationFrontendDirectory`), such as a
  library or a test project, or when the build does not build the frontend (`RunicApplicationFrontendBuildEnabled=false`,
  as the `runic-app` template's test project sets) and the frontend has no `@runic-artifex/views` installed. The
  generated files are unchanged. A project with generation off, or without Views, needs neither.
