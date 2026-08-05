# RunicToolkit.Hosting.CsWebUi.Mvvm tests

These executable, headless tests use a fake CsWebUi binary-binding boundary and
real retained MVVM sessions. They cover the one-binding handshake/open flow,
ordered patch and terminal pushes, cancellation, close teardown, client and
connection identity, strict frame rejection, and JavaScript-name validation.

```console
dotnet run --project tests/RunicToolkit.Hosting.CsWebUi.Mvvm.Tests
```
