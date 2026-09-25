# Getting started

Install the published templates from NuGet and create a starter app:

```sh
dotnet new install Runic.Application.Templates::<VERSION>
dotnet new runic-app-svelte --name MyApp --packageManager bun
cd MyApp
dotnet tool restore
dotnet runic doctor
dotnet run
```

Choose `react`, `vue`, `svelte`, or `angular` as the template short name. The
starter uses a .NET Window and View contract with a generated TypeScript client.
The frontend owns its rendered components; .NET owns the typed model and
operation lifetime. The template guide describes native prerequisites and
package-manager choices.

For the smallest source example, see [First Window](../../../../examples/first-window/README.md).
For existing projects, add `Runic.Application` and a host adapter described in
[`Runic.Application`](../../../../packages/dotnet/Runic.Application.Views/README.md).
