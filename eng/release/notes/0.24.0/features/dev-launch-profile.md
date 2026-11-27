## `dotnet runic dev` uses the launch profile, and passes the project's and the profile's arguments

`dev` now starts the Window with the first `Project` launch profile, as `dotnet run` and the IDEs do, so `dev` and F5
agree. `--no-launch-profile` runs the Window without one. `[dev] Launch profile:` names the profile, and
`[dev] Window arguments:` shows each part of the command line, also with `--dry-run`.

When the project sets `StartArguments` (for example `/mock` for Debug builds only) and the profile sets
`commandLineArgs`, `dotnet run` drops the profile's arguments, while Visual Studio and Runic DevKit pass both. `dev` now
passes both too, the project's first, and warns with `RAPPDEV1020`. The new doctor check `launch-arguments` lists what
each surface passes for the evaluated configuration. It also checks the optional `rax` object of a profile, whose
keys Runic DevKit reads.

See [Arguments for Debug builds](../../../docs/debugging.md#arguments-for-debug-builds).
