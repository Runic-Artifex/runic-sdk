- **Two development file formats take dotted names**, like every other Runic
  format identifier (`runic.views.fields/2`, `runic.application.tool.dev-session/1`):
  the Bridge generator's development source map (`obj/…/runic-bridge/sources.json`,
  which `/__runic/sources` serves and the session file's `sourceMap` names) is
  `runic.views.bridge-sources/1` instead of `runic-bridge-sources/1`, and the
  handler analysis that `/__runic/handlers` answers (`RunicHandlerAnalysis.format`
  of `@runic-artifex/vite-plugin-runic`) is `runic.vite.handlers/1` instead of
  `runic-handlers/1`. The content is unchanged. `@runic-artifex/vite-plugin-runic`
  still reads a map with the old name through 0.21 and serves it with the new
  one; a tool of its own that reads `sources.json` or `/__runic/sources` accepts
  the new name.
