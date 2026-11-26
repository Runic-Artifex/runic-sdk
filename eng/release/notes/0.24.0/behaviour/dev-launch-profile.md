- **`dotnet runic dev` runs the Window with the first launch profile.** Like `dotnet run`, `dev` now uses the first
  `Project` profile of `Properties/launchSettings.json` when no `--launch-profile` is given, so the profile's
  `commandLineArgs` and `environmentVariables` apply. Before, it used none. The template's first profile only sets
  `DOTNET_ENVIRONMENT=Development`, which `dev` sets anyway. For a profile that sets more, `--no-launch-profile` restores
  the 0.23 behaviour. A `-e` value of a name the profile also sets still wins: `dev` passes it to `dotnet run -e`.
