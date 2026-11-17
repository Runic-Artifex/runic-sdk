- **Deprecated: `onBridgeDraining` from `@runic-artifex/views/experimental`.**
  The close drain's flag is a root export of `@runic-artifex/views` from 0.22
  ([the side panel and the close drain are stable](#the-side-panel-and-the-close-drain-are-stable)).
  The old import still works in 0.22: `./experimental` re-exports the same
  function, marked `@deprecated`, so editors strike it through. 0.23 removes it;
  import `onBridgeDraining` from `@runic-artifex/views`. Remove `RUNICDESK104`
  and `RUNICDESK105` suppressions too, which no longer do anything.
