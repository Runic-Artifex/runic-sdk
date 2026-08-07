# RunicToolkit templates

Install the package and create one native CsWebUi application:

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
production frontend through `RunicToolkit.Hosting.Build`. The project has no
renderer-specific Toolkit adapter and creates its dependency lock after the
first `npm install`.
