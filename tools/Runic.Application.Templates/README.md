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
dotnet tool restore
dotnet runic-toolkit dev
```

The development command installs locked frontend dependencies when needed.
Framework templates use the binary application transport package's shared
high-level native-window builder surface.
