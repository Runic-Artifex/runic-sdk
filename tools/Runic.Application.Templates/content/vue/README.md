# RunicDesktopApp

This is a .NET 10 Runic Desktop application with a Vue/Vite frontend.

```console
dotnet run
dotnet run -- --smoke-test
dotnet runic dev --project RunicDesktopApp.csproj
dotnet runic doctor --project RunicDesktopApp.csproj
npm --prefix Frontend run typecheck
dotnet publish -c Release -r linux-x64 --self-contained true
```

The first managed build restores the committed frontend lock file with `npm ci`
and builds `Frontend/dist`; unchanged frontend inputs skip that work. `dotnet
runic dev` owns Desktop HMR, while `--smoke-test` is the headless application
test. Publish self-contained for a simpler deployment or add `-p:PublishAot=true`
for a smaller, faster-starting native binary at the cost of longer publish time.

`CounterBridgeHandler.cs` implements named generated C# commands.
`Frontend/src/counter-contract.ts` defines the matching Effect Schema contract,
and `counter-bridge.ts` selects the production Desktop channel or frontend-only
mock Layer. Vue owns only component state; one controller owns the Effect
runtime and its transport scope.

Run `npm --prefix Frontend run dev:mock` for browser-only development.
