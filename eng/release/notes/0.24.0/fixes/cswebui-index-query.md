- **A CS-WebUI page opened at `/?…` keeps its query (preview).** `Runic.Application.Views.CsWebUi` now needs CsWebUi
  2.5.0-beta.4.11, which bundles WebUI nightly `8a55352c2`. WebUI answered `/` and folder URLs with a redirect to
  their index file that dropped the query string, so a page opened at `http://localhost:<port>/?mode=x` arrived at
  `/index.html` without `mode` ([cs-webui#33](https://github.com/Runic-Artifex/cs-webui/issues/33)). The redirect now
  keeps it ([webui-dev/webui#729](https://github.com/webui-dev/webui/pull/729)); requesting `/index.html?…` directly
  is no longer needed. CS-WebUI's API is unchanged.
