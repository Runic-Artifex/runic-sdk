## `dotnet runic dev` and `doctor` start from a solution root

In a folder with a solution (`.sln` or `.slnx`) instead of one `.csproj`, such as the root of an application with a
library beside it, `dev` and `doctor` now evaluate the solution's projects and take its one Window project
(`RunicApplicationFrontendWindowProject=true`); `--project` may also name a solution or its folder. Before, they
stopped with `RAPPDEV1002` and asked for `--project`. A solution with several Window projects lists them and shows the
`--project` to choose one; a folder with one `.csproj` is still that project.

See [dotnet-runic](../../../tools/dotnet-runic/README.md).
