- **Deprecated: the host-named copy of the Views client script,
  `runic-desktop-views.js` and `runic-cswebui.js` (D072.11).** The host packages
  copy their Views client into `www/` as `runic-views.js` and, until 0.23, also
  under the host's own name. In 0.22 a page that loads the host-named copy still
  works, but logs one console warning; 0.23 removes the copy, which saves 5 KB
  (Runic Desktop) or 21 KB (CS-WebUI) per publish. Load `runic-views.js`
  instead: `<script src="runic-views.js"></script>` after `webui.js`. The
  templates already do. A project that references more than one host, such as
  Runic Desktop and CS-WebUI, sets `RunicApplicationHost` (`desktop` or
  `cswebui`), or the build copies no `runic-views.js` (warning
  `RUNICBRIDGE019`). An application that keeps its own copy of the script under
  the old name gets the same warning; rename that copy to `runic-views.js`.
