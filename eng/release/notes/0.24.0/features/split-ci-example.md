## Example: tests and the web on Linux, only the package on Windows

[`examples/split-ci`](../../../examples/split-ci/README.md) is a Runic Desktop application whose CI runs the tests and
`dotnet runic bundle` on Linux runners and uploads the web bundle as an artifact. Then a Windows job runs
`dotnet runic package windows --frontend-bundle`, and a Linux job runs `dotnet runic package deb --frontend-bundle`;
neither sets up Node.js. The packaging jobs run only on `main` and tags, after every Linux job passed (D082). The
example has two workflow files that differ only in the artifact actions and the default runner labels:
`.github/workflows/ci.yml` for GitHub Actions and Gitea Actions (Gitea Runner 3.0 or later), and
`.forgejo/workflows/ci.yml` for Forgejo, which needs Forgejo's forks of the artifact actions. `ci/check-workflows.mjs`
keeps them in step, and `ci/local-run.sh` runs both halves on one Linux machine, with a negative case: a ViewModel
changed after bundling stops the publish with `RUNICBRIDGE094`.
