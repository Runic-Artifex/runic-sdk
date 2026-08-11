# RunicToolkit templates

Install the package and create one native CS-WebUI application:

```console
dotnet new install RunicToolkit.Templates
dotnet new runic-toolkit-react -n MyApp
```

The available short names are `runic-toolkit-react`, `runic-toolkit-vue`,
`runic-toolkit-svelte`, and `runic-toolkit-angular`.

Every generated project has the same local development flow:

```console
dotnet build
dotnet run -- --smoke-test
```

Each template authors an Effect Schema contract, commits its generated C#
artifacts, uses the shared one-binding Application Bridge, and builds its
production frontend through `RunicToolkit.Hosting.Build`. React, Vue, and
Angular consume the framework-neutral controller directly. The Svelte template
uses the Svelte-5-only `@runic-artifex/svelte` lifecycle projection and the
official `@runic-artifex/vite-plugin-runic-toolkit` DevTools integration. Each
project creates its dependency lock after the first `npm install`.
