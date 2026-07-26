# Native MVVM Counter

**Difficulty:** Intermediate
**Frontend track:** Framework-neutral TypeScript
**Native host:** CsWebUi

This is the smallest complete demonstration of the production
`CsWebUiFrameChannel`. The browser opens a `webuitoolkit.mvvm/1` session,
projects a C# property and command, and exchanges every protocol frame through
one binary CsWebUi binding. It does not create ASP.NET Core endpoints or one
native callback per ViewModel command.

```bash
dotnet run --project samples/04-NativeMvvmCounter
```

For a headless C# check:

```bash
dotnet run --project samples/04-NativeMvvmCounter -- --smoke-test
```

The sample copies the repository-built framework-neutral MVVM ESM modules, the
CsWebUi channel, Bootstrap 5.3, and Font Awesome into its local web root. There
is no npm build or CDN dependency when it runs.

Start in `NativeCounterRoot.cs` to see the closed C# member vocabulary and
lifecycle bridge, then read `www/app.js` to see the corresponding protocol
projection.
