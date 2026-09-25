# Runic Views Svelte

A .NET 10 Window/View application with a Svelte frontend. The .NET
build generates typed View clients from your ViewModels and builds the frontend
through the Views MSBuild target.

Run these commands from the generated project directory:

- dotnet tool restore
- dotnet runic doctor --project RunicWindowApp.csproj
- dotnet run
- dotnet runic dev --project RunicWindowApp.csproj
- cd Frontend && __PACKAGE_MANAGER_NAME__ run typecheck
- dotnet publish -c Release -r linux-x64 --self-contained true

The generated project selects __PACKAGE_MANAGER_NAME__ and contains only that
manager's lock file. Install frontend dependencies with
__PACKAGE_MANAGER_NAME__ install before the first build. dotnet runic dev
restores dependencies, starts the native CS-WebUI Window, and coordinates
frontend development. A normal publish embeds the static frontend output and
does not need JavaScript on the target machine.

The C# Window owns a scoped WorkspaceViewModel; the selected Views are
resolved through Microsoft DI. The TypeScript clients are generated from those
ViewModels. React and Vue render generated page references directly.
Svelte and Angular use the typed ViewOutlet helpers from
@runic-artifex/views-svelte and @runic-artifex/views-angular.

See the Runic Views examples at
https://github.com/Runic-Artifex/runic-sdk/tree/main/examples for larger
applications with nested Views, routing, and multiple windows.
