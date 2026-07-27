# WebUIToolkitStarter

This is a .NET 10 native CsWebUi application using compiled cwhtml and HTMX.
Vite manages Bootstrap, Font Awesome, HTMX, CSS, and JavaScript; application
actions stay on the private CsWebUi binding.

```console
dotnet tool restore
dotnet webuitoolkit dev
```

The development command installs the locked frontend dependencies when needed.
Use `dotnet run` for a production-style asset build and native-window launch.
