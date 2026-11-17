- Size: a NativeAOT Runic Desktop application on Linux is about 0.43 MB smaller
  (11.79 → 11.36 MB for the size fixture's Desktop host). A trimmed or NativeAOT publish
  no longer links the macOS backend on Windows or Linux, nor `dotnet runic dev`'s live
  frontend directory and its file watcher. The new
  `Runic.Application.Views.Desktop.DevelopmentAssets` feature switch is off in trimmed
  and NativeAOT publishes; `dotnet runic dev` and other untrimmed builds keep the live
  directory, unchanged. Runic Desktop also uses fewer LINQ and collection types over
  enums and value tuples, and stores its file-extension media types as a compact table.
  Behaviour is unchanged.
  - Runic's three development-only feature switches
    (`Runic.Application.DevelopmentServerForwarding`, `Runic.Desktop.WebUiRemoteDebugging`
    and `Runic.Application.Views.Desktop.DevelopmentAssets`) are now off only in the
    trimmed or NativeAOT publish itself, including `dotnet publish --no-build` and
    `dotnet runic size`. A project that sets `PublishAot` or `PublishTrimmed` in its
    project file, as the .NET guidance recommends, keeps the development server fallback,
    the CS-WebUI DevTools endpoint and the live frontend directory in `dotnet build`,
    `dotnet run`, `dotnet runic dev` and IDE debugging.
