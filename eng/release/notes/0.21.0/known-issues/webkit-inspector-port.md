---
order: 10
---
- **WebKit's own `inspector://` port is open to other local users.** Under
  `dotnet runic dev` on Linux, WebKitGTK's remote inspector server, which the
  debugging adapter and GNOME Web connect to, cannot be filtered: another local
  user can connect to it, also while a debugger is attached, and a client that
  replaces another one can crash the Window on WebKitGTK 2.54. The adapter
  detaches its debugger when it notices a replaced client. On a shared machine,
  run `dotnet runic dev --no-debug-endpoint`. See
  [Debug the page's JavaScript on Linux](https://github.com/Runic-Artifex/runic-sdk/blob/v0.21.0/tools/dotnet-runic/README.md#debug-the-pages-javascript-on-linux-preview).
