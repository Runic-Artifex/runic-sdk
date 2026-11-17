- Size: a hosted Runic Desktop Window in component mode reads its component build's
  manifest (`_runic_component/runic-component.json`) from its own loopback surface with
  a small HTTP/1.1 GET of its own instead of `HttpClient`, so a trimmed or NativeAOT
  application no longer links the HTTP client and TLS stack for it. The request, the
  manifest checks and the 64 KiB and ten-second limits are unchanged. Together with the
  `Runic.Desktop.WebUiRemoteDebugging` feature switch, this takes about 2.8 MB off a
  Runic Desktop executable on Linux.
