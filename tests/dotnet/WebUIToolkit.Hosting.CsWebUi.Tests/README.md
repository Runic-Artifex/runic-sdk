# WebUIToolkit.Hosting.CsWebUi tests

These focused executable tests exercise the CsWebUi adapter without opening a
native browser or WebView. A runtime seam records native operations while the
tests cover:

- adapter option normalization and browser-mode validation;
- traversal-safe `app://` entry-point translation;
- process-wide serialized dispatcher behavior and reentrancy;
- local root, size, resizable, private-server, presentation, title, and
  navigation mapping;
- disconnected-client and process-exit close signaling;
- idempotent close, window disposal, and host disposal.

Run from the repository root:

```console
dotnet run --project tests/WebUIToolkit.Hosting.CsWebUi.Tests
```
