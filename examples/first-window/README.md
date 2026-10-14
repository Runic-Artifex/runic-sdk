# First Window

The smallest Runic application: one .NET ViewModel, one Window, and a plain
TypeScript page that shows and changes the ViewModel's state.

| File | What it does |
| --- | --- |
| [`CounterViewModel.cs`](CounterViewModel.cs) | A CommunityToolkit.Mvvm ViewModel with a `Count`, a writable `Step`, and an `Increment` command. |
| [`CounterWindow.cs`](CounterWindow.cs) | A one-line partial `CounterWindow` that tells the build to generate a client for `CounterViewModel`. |
| [`Program.cs`](Program.cs) | Registers the ViewModel, calls `AddRunicViews()`, opens `CounterWindow` with `host.OpenWindowAsync<CounterWindow>()`, and waits until it closes. |
| [`Frontend/src/app.ts`](Frontend/src/app.ts) | Calls `connectCounter()`, renders each snapshot, calls `increment()`, and writes `step` with `setStep()`. |

`dotnet build` compiles the C# project, inspects `CounterWindow`, and writes
the typed client to `Frontend/src/generated/counter.ts`. It then builds the
frontend and copies it to `www/` next to the application. This example commits
the generated file so the frontend type-checks before the first build; a
generated starter ignores it instead.

`RunicCsWebUiHost.Run` starts CS-WebUI, and `host.OpenWindowAsync<CounterWindow>()`
opens the Window with `index.html`, the default `RunicWindowOptions.StartPage`.
`window.WaitForCloseAsync()` returns when the user closes it. CS-WebUI uses an
installed browser in app mode (Chrome, Edge or another Chromium-based browser
works best), then the default browser, then the platform WebView. The
[host selection guide](https://docs.runic-artifex.eu/guides/desktop/host-selection/) describes
the Runic Desktop host for applications that need native windows.

`Frontend/index.html` loads `webui.js`, which CS-WebUI serves, and
`runic-views.js`, the Views client that `Runic.Application.Views.CsWebUi`
copies into `www/`. The page names no host:
[First Window on Runic Desktop](../first-window-desktop/README.md) serves the
same page with that host's client.

## Run it from this repository

```sh
bun run bootstrap
bun run example:first-window
```

On Linux with Nix, prefix commands with `direnv exec .` to use the SDK's
locked environment. Set `RUNIC_APPLICATION_SERVE_ONLY=1` to start the local
server without opening a browser; the application prints
`RUNIC_APPLICATION_URL=<url>` and stops when it reads a line from standard input.

## Copy it into your own project

Inside this repository the project builds the Runic packages from source with
`ProjectReference` and explicit `Import` lines. To use it elsewhere, copy the
folder, then replace the `Import` and `ProjectReference` lines in
`FirstWindow.csproj` with package references:

```xml
<ItemGroup>
  <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
  <PackageReference Include="Runic.Application.Views.CsWebUi" Version="<VERSION>" />
  <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.12" />
</ItemGroup>
```

Use the version shown in the [package catalog](https://docs.runic-artifex.eu/packages/).

The generated client imports the browser runtime `@runic-artifex/views`, so
`Frontend/package.json` declares it. In this repository it points at the
runtime's source with `file:../../../packages/web/views`; the repository's
root install resolves it, and builds here install nothing in `Frontend`.
Replace it with the same version from npm, then update the lock file:

```sh
cd Frontend
bun add @runic-artifex/views@<VERSION>
```

The frontend build uses Bun because `Frontend/bun.lock` is committed; a missing
`node_modules` is installed during the first build. To start a new application,
prefer the [project templates](https://docs.runic-artifex.eu/getting-started/).

## Checks

The SDK's CI runs these checks. The browser check covers the first snapshot, a
command, a property write, and a reload. The package check packs the Runic
packages, restores a copy of this example outside the repository, and runs the
same browser journey. CS-WebUI Window lifetime probes live in
[`tests/fixtures/application/cswebui-window-probes`](../../tests/fixtures/application/cswebui-window-probes/Program.cs).

```sh
dotnet build examples/first-window/FirstWindow.csproj -c Release
node examples/first-window/browser-smoke.mjs
node examples/first-window/package-smoke.mjs
```
