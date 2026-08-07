# Native CsWebUi end-to-end gate

This executable starts the real native CsWebUi server without launching a
desktop window, attaches the production binary Application Bridge, opens the page in
the Nix-pinned headless Chromium, executes a C# command, and verifies the
updated DOM emitted by Chromium.

It requires the repository direnv shell so the native WebUI library and
`WEBUI_BROWSER_PATH` point at the pinned library and browser.

Run the real native-browser gate with:

```console
nix develop --command dotnet run \
  --project tests/RunicToolkit.Hosting.CsWebUi.NativeE2E \
  --configuration Release
```

The main `eng/verify.sh` pipeline runs managed contract tests; invoke this gate
explicitly when the pinned browser and WebUI library are available. NativeAOT
compatibility is verified separately by
`RunicToolkit.ApplicationBridge.AotSmoke` and the isolated package-consumer
publication gate.
