## Web bundles: build the frontend in one CI job, publish in another

`-p:RunicApplicationFrontendBundleDirectory=<dir>` makes a build write a web bundle (D082): after its build, every
project that declares Views writes its built page (`projects/<assembly>/www/`), its generated clients
(`projects/<assembly>/clients/`) and its entry of `web-bundle.json`, format `rax.web-bundle/1`. The entry records the
project's View fingerprints from the contract IR, its host, `NODE_ENV`, lock file hash and `@runic-artifex/views`
version, and the manifest the .NET SDK, Node.js and commit. The projects of a solution build, such as a WPF
application's model project and the application, or a client library and its application, write their entries side by
side. A project whose frontend build is off cannot write its page (`RUNICBRIDGE092`). The format is in
[specs/application/web-bundle.md](../../../specs/application/web-bundle.md); see
[Building the frontend in another job](../../../packages/dotnet/Runic.Application.Views/README.md#building-the-frontend-in-another-job).
