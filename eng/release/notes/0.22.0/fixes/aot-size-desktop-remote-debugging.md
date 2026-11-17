- Size: a trimmed or NativeAOT Runic Desktop application no longer links the CS-WebUI
  Window's Development DevTools Protocol endpoint. The new
  `Runic.Desktop.WebUiRemoteDebugging` feature switch is off in trimmed and NativeAOT
  publishes, so the trimmer removes the endpoint and the HTTP client of its
  `/json/version` check; such a build opens no endpoint, even in `Development`.
  `dotnet runic dev` and other untrimmed builds keep it, unchanged. Together with the
  hosted region's manifest read without `HttpClient`, this takes about 2.8 MB off a
  Runic Desktop executable on Linux.
