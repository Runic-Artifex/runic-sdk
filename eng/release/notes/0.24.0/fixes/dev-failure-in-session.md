- **A failure that stops `dotnet runic dev` reaches VS Code's Problems panel through the session file.** VS Code removes
  a background task's problems when its process ends, so a `dev` that stopped at its start (a failed first build, or
  `RAPPDEV1018` after a frontend package's build error) left Problems empty when the template's `runic: dev` task ran it,
  from F5 or from Terminal > Run Task, and Runic DevKit's dependency tree still said "built". `dev` now writes the failure
  into its session file before it exits: a `failure` member with `dev`'s code and message, the project and the frontend
  package that failed, and the diagnostics of the step that failed (file, line, column, severity, code and message; at
  most 100). Runic DevKit 0.24 reads it and keeps the errors in Problems and the package "failed" in its tree, however the
  task was started. `failure` is null while `dev` runs and after a stop without a failure; readers that do not know it
  ignore it (`runic.application.tool.dev-session/1` only adds properties). The template's `.vscode/tasks.json`, its
  README, `docs/debugging.md` and the `dotnet runic dev` README now say what Problems shows with and without DevKit.
