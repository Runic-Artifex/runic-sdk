- **`onBridgeDraining` moved to the root entry of `@runic-artifex/views`**
  ([the side panel and the close drain are stable](#the-side-panel-and-the-close-drain-are-stable)).
  `@runic-artifex/views/experimental` no longer exports it; import it from
  `@runic-artifex/views`. Remove `RUNICDESK104` and `RUNICDESK105` suppressions,
  which no longer do anything.
