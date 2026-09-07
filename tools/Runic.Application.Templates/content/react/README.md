# RunicDesktopApp

This is a .NET 10 Runic application with a React/Vite frontend.

```console
dotnet tool restore
dotnet run
dotnet run -- --smoke-test
dotnet runic dev --project RunicDesktopApp.csproj
dotnet runic doctor --project RunicDesktopApp.csproj
cd Frontend && __PACKAGE_MANAGER_NAME__ run typecheck && cd ..
dotnet publish -c Release -r linux-x64 --self-contained true
```

The template selected __PACKAGE_MANAGER_NAME__ and includes its exact lock file.
The first managed build performs a frozen dependency restore and builds
`Frontend/dist`; unchanged frontend inputs skip that work. `dotnet
runic dev` coordinates host startup and frontend HMR, while `--smoke-test` is the headless application
test. The selected package manager is required for development and publishing,
but the published application embeds the static frontend and does not require a
JavaScript runtime on the target machine. Publish self-contained for a simpler deployment or add `-p:PublishAot=true`
for a smaller, faster-starting native binary at the cost of longer publish time.

Vite+ users can keep the generated package-manager declaration and run `vp
install --frozen-lockfile` plus `vp run dev|build|typecheck` as an optional
facade over these scripts.

`CounterBridgeHandler.cs` implements named generated C# commands.
`Frontend/src/application.bridge.ts` is the handwritten Effect Schema contract. The Vite plugin
generates `Contract/bridge.ir.json` and the fingerprint-only frontend facade,
and `counter-bridge.ts` selects the selected native host channel or frontend-only
mock Layer. React owns only component state; one controller owns the Effect
runtime and its transport scope.

Run `cd Frontend && __PACKAGE_MANAGER_NAME__ run dev:mock` for browser-only development.

The selected host is `__RUNIC_HOST__`. Both hosts use the same application members,
generated bridge and UI. Switch with `dotnet runic dev --host desktop` or
`dotnet runic dev --host cswebui`; keep restore enabled when changing hosts.
For a normal build or publish, pass `-p:RunicHost=desktop` or
`-p:RunicHost=cswebui`, or change `RunicHost` in the project. CS-WebUI uses its
native server without Desktop or ASP.NET Core. Desktop adds isolated surfaces,
presentation policy and native close coordination.

The development command supplies an explicit entry-document snapshot from the
frontend server; other assets remain manifest-bound. Published applications use
the embedded archive and require no development server.
