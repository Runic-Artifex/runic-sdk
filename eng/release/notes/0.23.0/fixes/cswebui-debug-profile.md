- **CS-WebUI: the Development DevTools Protocol endpoint runs in a profile of the run's own.** When
  `RUNIC_WEBUI_REMOTE_DEBUGGING_PORT` opens the endpoint, the browser no longer uses WebUI's
  `.WebUI/WebUI<Browser>Profile`, which every WebUI application of the user shares and which persists. It runs in a new
  `runic-cswebui-devtools-*` directory in the user's temporary folder (mode 0700 on Linux and macOS), which the Window
  deletes when that browser exits. Other applications no longer join the browser that holds the port, and its cookies
  and storage do not reach the next run. The process's windows still share one browser: a later window joins it and
  reports nothing of its own, instead of asking for a port that its browser never opens. The Window reads a free port
  (`0`) from `DevToolsActivePort` in that profile. A fixed port, such as the template's 9225, is reported only when the
  browser process WebUI started holds the listener on 127.0.0.1, checked through `/proc` on Linux,
  `GetExtendedTcpTable` on Windows and `lsof` on macOS. Otherwise the Window warns and prints no
  `RUNIC_APPLICATION_DEBUG_ENDPOINT` line. A `WebUiWindow.SetProfile` in `ConfigureWindow` replaces the run's profile.
  See
  [CS-WebUI pages in a JavaScript debugger](../../../docs/debugging.md#cs-webui-pages-in-a-javascript-debugger).
