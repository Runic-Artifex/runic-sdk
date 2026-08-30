# Linux native-quality receipt

`linux-x64.json` is a retained, two-run receipt for the exact local Linux x64
profile that produced it. It binds the operating system, architecture, .NET
SDK/runtime, Chromium version, Nix-pinned WebKitGTK package, .NET and
TypeScript package versions, Runic Desktop contract version, and SHA-256
fingerprints of the implementation and evidence sources.

Regenerate a candidate receipt only from the pinned desktop development shell:

```console
nix develop -c node eng/verify-linux-native-quality-receipt.mjs run-twice > /tmp/linux-x64.json
```

Validate the retained receipt against the current profile without treating a
different browser, WebKitGTK package, OS, runtime, architecture, Windows, or
macOS as equivalent:

```console
nix develop -c node eng/verify-linux-native-quality-receipt.mjs verify-receipt evidence/native-quality/linux-x64.json
```

The receipt composes existing behavioral tests: two isolated surfaces, 24
concurrent server-window cycles, request and window stream cancellation,
two authenticated bridge sessions with distinct IDs, owned-browser cleanup and
restart, an embedded WebKitGTK restart, and a 150,000-byte Chromium bridge
round trip. The last is structural capacity evidence, not a latency budget.

It deliberately does not certify application accessibility, calibrated
performance, profiler-backed memory budgets, WebView2, or WKWebView. Those
exclusions are part of the fail-closed receipt and remain W110-002 work.
