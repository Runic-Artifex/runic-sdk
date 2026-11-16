---
order: 30
---
- **IDE JavaScript debugging is a preview on Linux and goes through Chrome on
  macOS.** The Linux adapter stays a preview until its CI job has run through a
  release, and Rider's JavaScript debugger is not verified with it. WKWebView
  opens no DevTools Protocol endpoint, so on macOS the IDE debugs the page in
  Chrome with `dotnet runic dev --browser`, without native window features, or
  Safari's Web Inspector debugs the Window. On Windows, WebView2 ignores the
  endpoint's variable in an elevated Window: run `dev` and the IDE without
  elevation.
