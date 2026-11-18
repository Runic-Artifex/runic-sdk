- **Async commands no longer add a state machine each to a NativeAOT executable.** A generated Bridge ran each
  CommunityToolkit async command, and each ReactiveUI command with a result or a completion, in an async lambda of its
  own. Bridges now pass a plain lambda to one runner per assembly that the generator writes beside them
  (`RunicShared.Toolkit.Bridge.g.cs`, `RunicShared.Commands.Bridge.g.cs`), with the same steps in the same order:
  the token check, `Cancel` registration and the typed `ExecuteAsync(T)` call for CommunityToolkit, then the await
  and the result's encoding. The Toolkit `IsRunning` subscription is shared the same way. In the research fixture of
  #1109 a ViewModel with three async commands costs 32.5 KB instead of 43.2 KB on linux-x64 (56.3 KB in 0.21); in the
  scaling suite's `aot-size` fixture 18.2 KB instead of 21.3 KB (25.5 KB in 0.21). ReactiveUI stream commands are
  unchanged. `ToolkitCommandInspector.DescriptorFor` keeps its signature; the descriptor it emits now calls the
  generated runner, which the Runic generator writes whenever a Bridge has a CommunityToolkit async command. Generated
  TypeScript is byte-identical. Part of #1109.
