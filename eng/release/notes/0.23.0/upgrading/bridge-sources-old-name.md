- **Removed: vite-plugin-runic's reader of the 0.20 source-map name `runic-bridge-sources/1` (D077.18).** 0.21 renamed
  the Bridge generator's development source map (`sources.json`, which `/__runic/sources` serves) to
  `runic.views.bridge-sources/1` and kept reading the old name for one minor release. `@runic-artifex/vite-plugin-runic`
  now reads only the new name; a map with the old one counts as no map, so Runic Inspect shows no source locations
  and `/__runic/open` cannot open them. Only a 0.20 generator writes the old name: rebuild the application with 0.23.0,
  which regenerates the map.
