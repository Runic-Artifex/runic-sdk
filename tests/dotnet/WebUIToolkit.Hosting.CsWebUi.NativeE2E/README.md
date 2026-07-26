# Native CsWebUi end-to-end gate

This executable starts the real native CsWebUi server without launching a
desktop window, attaches the production binary MVVM bridge, opens the page in
the Nix-pinned headless Chromium, executes a C# command, and verifies the
updated DOM emitted by Chromium.

It requires the repository direnv shell so the native WebUI library and
`WEBUI_BROWSER_PATH` point at the pinned library and browser.

Run the complete, Native-AOT-published gate with:

```console
./eng/verify-cswebui-native-e2e.ps1
```

The script keeps RID-specific restore state under `obj`, verifies committed
portable lock files remain unchanged, publishes with full trimming and Native
AOT, and executes both this binary-transport gate and the compiled SimpleTodo
HTMX browser gate against Chromium. The main `eng/verify.ps1` gate runs it
automatically whenever both pinned paths are available.
