## `dotnet runic bundle`, and `package --frontend-bundle` without Node.js

For a CI whose Linux jobs build and test everything web and whose Windows or macOS job only publishes (split CI, D082):

- **`dotnet runic bundle`** builds the project or solution with `RunicApplicationFrontendBundleDirectory`, checks the
  web bundle (`rax.web-bundle/1`) it wrote, and prints each project's kind, Views and the files, size and SHA-256 of its
  page; `--format json` gives `runic.application.tool.bundle/1`. Its output directory must be new, empty or an earlier
  bundle (`RAPPBUNDLE1001`–`1004`).
- **`dotnet runic package <target> --frontend-bundle <dir>`** publishes with the bundle's pages through
  `RunicApplicationFrontendPrebuiltDirectory`, so the runner needs neither Node.js nor Bun. `package` checks the bundle
  first, and the build compares each View's fingerprint with its own (`RUNICBRIDGE094`).
- **`package` passes MSBuild properties through**: `--property NAME=VALUE` or `-p:NAME=VALUE`, as `dotnet publish`
  takes them, reach the identity's evaluation and the publish of every target but `flathub`. `-p` with a separate value
  stays `--project`. Properties that `package` sets itself are refused (`RAPPPKG1008`).
- **`dotnet runic doctor --bundle <dir>`** checks a downloaded bundle offline, as its `web-bundle` check: the manifest,
  the hash of each page and an entry for the project.

See [Write a web bundle](../../../tools/dotnet-runic/README.md#write-a-web-bundle).
