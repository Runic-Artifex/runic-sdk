- **Side panel on Wayland and on transparent GTK 3 windows.** On Wayland the left edge is refused with
  `PositionUnavailable`, and the right edge is not checked in CI, which has no headless compositor. A transparent
  GTK 3 window refuses the panel with `Unsupported`. See
  [Side panel: Known limits](../../../docs/side-panel.md#known-limits).
