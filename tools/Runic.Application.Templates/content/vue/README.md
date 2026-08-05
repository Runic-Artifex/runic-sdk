# RunicToolkitStarter

This is a .NET 10 native CsWebUi application with a Vue SFC/Vite frontend.

```console
dotnet tool restore
dotnet runic-toolkit doctor
dotnet runic-toolkit dev
```

The counter is a real native MVVM roundtrip: `CounterViewModel.cs` owns
validation, history, derived state, the command, and its C#-first contract
attributes, which generate the trim-safe adapter and typed Vue contract; and
`Frontend/src/App.vue` uses an ordinary SFC, Composition API, and generated
composable.

The development command installs the locked frontend dependencies and runs Vue
SFC HMR without replacing the native window or C# ViewModel.
For frontend-only work, `cd Frontend && npm run dev:mock` runs the generated
contract against the development-only production-protocol fixture in
`src/counter.mock.ts`; its summary is visibly marked `MOCK`.
`dotnet build --configuration Release` tests the optimized asset pipeline;
`dotnet publish --configuration Release` produces the publish layout.
