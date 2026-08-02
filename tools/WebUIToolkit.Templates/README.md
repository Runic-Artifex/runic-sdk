# WebUIToolkit templates

Install the package and create one native CsWebUi application:

```console
dotnet new install WebUIToolkit.Templates
dotnet new webuitoolkit-cwhtml -n MyApp
dotnet new webuitoolkit-csharp-markup -n MyCsharpMarkupApp
```

The available short names are `webuitoolkit-cwhtml`, `webuitoolkit-csharp-markup`,
`webuitoolkit-react`, `webuitoolkit-vue`, `webuitoolkit-svelte`, and
`webuitoolkit-angular`.

Every generated project has the same local development flow:

```console
dotnet tool restore
dotnet webuitoolkit dev
```

The development command installs locked frontend dependencies when needed.
The compiled-markup templates keep HTMX requests on the private CsWebUi binding while
Vite owns JavaScript and CSS dependencies. Framework templates use the binary
MVVM transport package's shared high-level native-window builder surface.
