# First Window

A first-project sketch built with the replacement View bridge. The .NET side
has a CommunityToolkit `CounterViewModel` and an explicit partial
[`CounterWindow`](CounterWindow.cs). The generator discovers the Window and
emits a typed C# attachment plus an ordinary TypeScript module. The browser
imports `connectCounter`, subscribes to state, calls `increment()`, and writes
`step` with `setStep()`. No old Application Bridge package or
`[RunicViewModel]` marker is used.

```sh
direnv exec . dotnet build examples/first-window/FirstWindow.csproj -c Release
direnv exec . node examples/first-window/browser-smoke.mjs
direnv exec . node examples/first-window/package-smoke.mjs
direnv exec . dotnet run --project examples/first-window/FirstWindow.csproj -c Release --no-build
```

The browser check covers initial state, a command, a writable property, and
reload. The package check packs the three required View packages, restores a
temporary consumer outside this tree, generates its contract, runs the same
browser journey, and removes its temporary files. The project uses CS-WebUI
as its native host and Microsoft DI for the
window scope. `OpenWindow` constructs `CounterWindow` before attaching its
root Bridge. See
[the cutover plan](../../VIEW-BRIDGE-CUTOVER.md).
