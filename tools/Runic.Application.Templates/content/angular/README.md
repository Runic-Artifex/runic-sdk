# RunicToolkitStarter

This is a .NET 10 native CS-WebUI application with an Angular frontend.

```console
dotnet build
dotnet run
dotnet run -- --smoke-test
```

`CounterBridgeHandler.cs` implements named generated C# commands.
`Frontend/src/counter-contract.ts` defines the matching Effect Schema contract,
and `counter-bridge.ts` selects the production CS-WebUI or frontend-only mock
Layer. Angular owns only signal state; one controller owns the Effect runtime.

Run `npm --prefix Frontend run dev:mock` for browser-only development.
