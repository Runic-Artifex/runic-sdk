- **The session file's `browser.url` is a pattern, not an address.** Since 0.21 the `dotnet runic dev --browser`
  session file (`runic.application.tool.dev-session/1`) listed the page's address in `browser.url` and
  `browser.windows[].url`. In 0.23 the address has a secret label for each run (#1187), so these properties hold a
  pattern with `*` for the label, `http://*.localhost:9224/`, which a browser cannot open. The schema name stays `/1`,
  as no first-party reader uses the property; this is the one change to an existing property within it. A reader that
  opened `browser.url` opens the new `browser.codeFile` instead, which admits the browser and then opens the address;
  the patterns still serve to match the page by URL, such as in a launch configuration's `urlFilter`.
