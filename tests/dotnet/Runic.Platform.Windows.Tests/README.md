# Windows provider verification

Portable deterministic checks:

```sh
dotnet run --project tests/dotnet/Runic.Platform.Windows.Tests
```

On Windows x64, in an interactive desktop, run `--native` to exercise actual Win32
clipboard exchange with an independent child process, null/empty text, Unicode,
bounded reads, contention and retry:

```powershell
dotnet run --project tests/dotnet/Runic.Platform.Windows.Tests -- --native
dotnet publish tests/dotnet/Runic.Platform.Windows.Tests -c Release -r win-x64 -p:PublishAot=true
# Run the published Runic.Platform.Windows.Tests.exe --native as well.
```

`--native-settings` reads the signed-in user's appearance through the provider and
checks the color scheme against `AppsUseLightTheme` when high contrast is off. CI
runs it with JIT and NativeAOT.

This modifies the test desktop's clipboard. These tests do not satisfy selected-file,
keyboard, screen-reader or display acceptance. Retain exact binary hashes and the
source revision with receipts for both JIT and AOT executions.
