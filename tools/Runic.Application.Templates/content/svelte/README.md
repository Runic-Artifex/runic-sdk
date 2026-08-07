# RunicToolkitStarter

This is a .NET 10 native CsWebUi application with a Svelte/Vite frontend.

```console
dotnet build
dotnet run
dotnet run -- --smoke-test
```

`CounterBridgeHandler.cs` implements named generated C# commands.
`Frontend/src/counter-contract.ts` defines the matching Effect Schema contract,
and `counter-bridge.ts` selects the production CsWebUi or frontend-only mock
Layer. `@runic-artifex/svelte` owns Svelte 5 rune projection and component-tree
lifecycle while one controller owns the Effect runtime. The Runic Vite plugin
adds HMR persistence and the official Vite DevTools panel.

Run `npm --prefix Frontend run dev:mock` for browser-only development.
