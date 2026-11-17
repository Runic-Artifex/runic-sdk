- **The `runic-app` template no longer has the `<Project> (WebView2 script debugging)`
  launch profile** (Runic Desktop, added in 0.20). It does not work in Visual Studio 2026
  (18.8 and 18.12): F5 hands the profile to Visual Studio's JavaScript adapter
  (`jsWebView2Debugging`), which starts the Window without the profile's environment
  variables. The Window then runs outside Development, Runic clears
  `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`, and the adapter cannot connect and stops the
  Window after about 20 seconds. In an existing project, delete the profile, the entry
  with `"jsWebView2Debugging": true`, from `Properties/launchSettings.json`, and keep
  `<Project>`. To stop at breakpoints in `Frontend/src` in Visual Studio, use Debug the
  Window of [Runic DevKit for Visual Studio](https://github.com/Runic-Artifex/runic-editors/blob/main/visualstudio/README.md)
  (0.22), which starts the Window under the .NET debugger and attaches the JavaScript
  debugger to the page. The VS Code configurations (`Window + WebView2 (Windows)`) and
  `.vsconfig` are unchanged. See
  [Visual Studio](https://github.com/Runic-Artifex/runic-sdk/blob/v0.22.0/docs/debugging.md#visual-studio).
