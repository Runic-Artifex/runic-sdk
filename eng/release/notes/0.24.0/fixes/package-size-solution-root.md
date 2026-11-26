- **`dotnet runic package` and `size` start from a solution root.** In a folder with a solution (`.sln` or `.slnx`)
  instead of one `.csproj`, they stopped with `RAPPDEV1002: No .csproj was found in the directory. Use --project.`
  They now select the project as `dev` and `doctor` do: the solution's one Window project, or a list of the Window
  projects with the `--project` to choose one. `--project` may name a solution or its folder, and `-p <path>` is still
  the project beside `-p:NAME=VALUE`.
