- Size: a NativeAOT Runic Desktop application keeps less that it does not use
  (runic-sdk#1109; the size fixture's Desktop host on Linux 11.36 → 10.75 MB):
  - Hosted mode (the region guest) is kept only by an application that creates
    `RunicGuestOptions`; an application that is never a guest no longer carries it (−0.51 MB).
  - The page script of `/webui.js` is compiled in minified, as UTF-8, and the macOS window
    chrome and WebKitGTK keyboard scripts only in builds whose window code uses them.
  - `RunicDesktopServer=None` now turns the presentation server off, as documented for an
    executable that opens no window: a trimmed or NativeAOT build keeps neither server, also
    when it references `Runic.Desktop.Kestrel` (−4.6 MB in that case). A window in such an
    application fails with `presentation-server-unavailable`.
  - The new MSBuild property `RunicDesktopRegionHost` (default `true`) lets an application
    whose pages declare no hosted regions leave the region host out (`false`).
