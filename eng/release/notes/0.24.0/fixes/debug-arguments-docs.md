- **Arguments for Debug builds are documented.** An argument that only Debug builds get, such as `/mock`, belongs in
  MSBuild's `StartArguments` in a Debug-conditioned `PropertyGroup`: `dotnet run`, `dotnet runic dev`, Visual Studio
  and Runic DevKit read it, and Release builds don't get it. A launch profile's `commandLineArgs` can't be limited to
  Debug. [Arguments for Debug builds](../../../docs/debugging.md#arguments-for-debug-builds) shows which surface reads
  which, and what happens when the project and the profile both set arguments. The `dotnet-runic` README also says how
  to run `dotnet runic` at a solution root: the tool manifest belongs there.
