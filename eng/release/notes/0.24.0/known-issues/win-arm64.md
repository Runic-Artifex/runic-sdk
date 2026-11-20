- **On Windows on Arm (win-arm64), drag-and-drop and the side panel are not CI-verified.** The hosted runner
  delivers no `SendInput` to WebView2, so drops onto and drags out of WebView2 are not checked there
  ([#391](https://github.com/Runic-Artifex/runic-sdk/issues/391)), and its desktop shows GDI nothing, so neither is
  the side panel's frame capture or Tab between the views
  ([Side panel: Known limits](../../../docs/side-panel.md#known-limits)). The rest of win-arm64 runs in CI.
