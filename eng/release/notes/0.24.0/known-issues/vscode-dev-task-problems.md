- **VS Code: a failure that ends `dotnet runic dev` leaves Problems empty when VS Code runs the template's task.**
  VS Code removes a background task's problems when its process ends, so a `dev` that stops at its start (a failed
  first build, `RAPPDEV1018` after a frontend package's build error) shows its error only in the `runic: dev` task's
  terminal, also when F5 runs the task. Errors while `dev` keeps running still appear in Problems. Runic DevKit 0.24's
  **Start dotnet runic dev** runs the template's `dev` tasks in a terminal of its own and keeps the error in Problems and
  the package's failed state in its dependency tree. The template's `.vscode/tasks.json`, its README, `docs/debugging.md`
  and the `dotnet runic dev` README now say so.
