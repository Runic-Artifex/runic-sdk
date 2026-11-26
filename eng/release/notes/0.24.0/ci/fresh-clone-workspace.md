- **A fresh-clone test of a library and an application in a JavaScript workspace.** The repository's
  `Directory.Build.props` turns the frontend install off for every in-repository project, so no check installed a
  workspace or built a compiled library package, and the workspace failures that 0.24 fixes went unnoticed.
  `examples/shared-library-clients/fresh-clone-smoke.mjs`, in the Views journeys job, copies the shared-library example
  out of the repository as a Bun, npm and pnpm workspace with a compiled package, then checks three things from a fresh
  clone: `dotnet runic dev` reaches Ready, `dotnet build` passes, and a second build installs and builds no frontend.
