- **WebKit's own `inspector://` port is open to other local users.** Under `dotnet runic dev` on Linux, WebKitGTK's
  remote inspector server, which the debugging adapter and GNOME Web connect to, cannot be filtered: another local
  user can connect to it, also while a debugger is attached, and a client that replaces another one can crash the
  Window on WebKitGTK 2.54. On a shared machine, run `dotnet runic dev --no-debug-endpoint`. See
  [The browser's developer tools per engine](../../../docs/debugging.md#the-browsers-developer-tools-per-engine).
