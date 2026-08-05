# RunicToolkitStarter

This is a .NET 10 native CsWebUi application using C# markup and HTMX. Its
`.cwuix` view supports ordinary C# expressions inside markup and markup nested
inside conditionals, lambdas, LINQ, arguments, and collection expressions.

```console
dotnet tool restore
dotnet runic-toolkit doctor
dotnet runic-toolkit dev
```

While `dev` is running, edit `Views/Counter.cwuix`. Compatible renderer edits
retain the process and `CounterViewModel`, then refresh only `#counter`; changes
to component signatures, captures, or HTMX registrations restart safely.

Inspect deterministic build artifacts with:

```console
dotnet runic-toolkit inspect --artifact manifest
dotnet runic-toolkit inspect --artifact generated
```
