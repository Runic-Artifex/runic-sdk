- **Tests lease the loopback ports they hand to a child.** A port taken by binding port 0 and closing the listener could
  go to any other bind or connection before the child bound it (#1257). `tests/support/TestPorts.cs` and
  `tests/support/ports.mjs` lease a random port outside the ephemeral range that no socket uses, held until the child
  starts; the tests' private free-port helpers, the inspector, WebView2 and web-content smokes and js-debug's DAP
  scenarios use them.
