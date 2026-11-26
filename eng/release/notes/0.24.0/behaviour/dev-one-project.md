- `dotnet runic dev` passes `RunicApplicationFrontendDevSessionProject` instead of `RunicApplicationFrontendBuildEnabled=false`,
  `RunicApplicationFrontendCopyEnabled=false` and `RunicApplicationFrontendInstallEnabled=false`, and the build installs
  the frontend packages. With `--no-restore` it passes `RunicApplicationFrontendInstallMode=Never`; with `--no-build`
  nothing installs them, and missing packages stop `dev` with `RAPPDEV1008`. On a fresh clone `dev` reads the Vite
  build inputs after the build that installs Vite. With Views targets older than the tool, `dev` warns with
  `RAPPDEV1019` and keeps the old behaviour.
