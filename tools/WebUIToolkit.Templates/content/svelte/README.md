# WebUIToolkitStarter

This is a .NET 10 native CsWebUi application with a Svelte/Vite frontend.

```console
dotnet tool restore
dotnet webuitoolkit doctor
dotnet webuitoolkit dev
```

The counter is a real native MVVM roundtrip: `CounterViewModel.cs` owns
validation, history, derived state, the command, and its C#-first contract
attributes, which generate the trim-safe adapter and typed Svelte contract; and
`Frontend/src/App.svelte` uses Svelte 5 props, current event attributes,
context ownership, and generated stores.

The development command installs the locked frontend dependencies and runs
Svelte HMR without replacing the native window or C# ViewModel.
For frontend-only work, `cd Frontend && npm run dev:mock` runs the generated
contract against the development-only production-protocol fixture in
`src/counter.mock.ts`; its summary is visibly marked `MOCK`.
`dotnet build --configuration Release` tests the optimized asset pipeline;
`dotnet publish --configuration Release` produces the publish layout.
