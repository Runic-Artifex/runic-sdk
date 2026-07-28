# WebUIToolkitStarter

This is a .NET 10 native CsWebUi application using Angular's application
builder for production and its supported development-server builder for HMR.

```console
dotnet tool restore
dotnet webuitoolkit doctor
dotnet webuitoolkit dev
```

The counter is a real native MVVM roundtrip: `CounterViewModel.cs` owns
validation, history, derived state, the command, and its C#-first contract
attributes, which generate the trim-safe adapter and typed Angular contract; and
`Frontend/src/main.ts` plus `app.html` use a standalone component, dependency
injection, signals, and generated providers.

The development command installs the locked dependencies and supervises
`ng serve` without replacing the native window or C# ViewModel.
For frontend-only work, `cd Frontend && npm run dev:mock` selects Angular's
dedicated mock application entrypoint and runs the generated contract against
`src/counter.mock.ts`; its summary is visibly marked `MOCK`.
`dotnet build --configuration Release` tests the optimized AOT asset pipeline;
`dotnet publish --configuration Release` produces the publish layout.
