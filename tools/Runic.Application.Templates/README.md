# RunicToolkit templates

Install the package and create one native CsWebUi application:

```console
dotnet new install RunicToolkit.Templates
dotnet new runic-toolkit-cwhtml -n MyApp
dotnet new runic-toolkit-csharp-markup -n MyCsharpMarkupApp
```

The available short names are `runic-toolkit-cwhtml`, `runic-toolkit-csharp-markup`,
`runic-toolkit-react`, `runic-toolkit-vue`, `runic-toolkit-svelte`, and
`runic-toolkit-angular`.

Every generated project has the same local development flow:

```console
dotnet tool restore
dotnet runic-toolkit dev
```

The development command installs locked frontend dependencies when needed.
The compiled-markup templates keep HTMX requests on the private CsWebUi binding while
Vite owns JavaScript and CSS dependencies. Framework templates use the binary
MVVM transport package's shared high-level native-window builder surface.
