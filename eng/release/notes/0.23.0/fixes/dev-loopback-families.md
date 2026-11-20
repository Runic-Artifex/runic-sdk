- **`dotnet runic dev` listens on both loopback addresses.** `--browser`'s address and, on Linux, the CDP adapter for
  JavaScript debuggers listened on 127.0.0.1 alone, while Chromium connects to a `.localhost` name on `[::1]` first, and
  a debugger told `localhost` may do the same. They now listen on 127.0.0.1 and `[::1]` on the same port, with the same
  `Host`, `Origin`, code and cookie rules and, on Linux, the same check of the connecting user on both. A port counts as
  free only when it is free on both: when another program holds 9224 on either address, `--browser`'s address moves to
  a port free on both and the `[dev] Browser:` line says so, and the adapter moves off 9222 as before. A system without
  IPv6 keeps 127.0.0.1 alone. The template's `Attach to the page (JavaScript)` configuration now names
  `"address": "127.0.0.1"`; add it to a configuration generated earlier. See the
  [security guide](../../../docs/security.md#development-server).
