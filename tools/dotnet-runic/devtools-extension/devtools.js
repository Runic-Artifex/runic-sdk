// The extension's DevTools page: adds the "Runic" panel and the "Runic" pane of the Elements panel. Both read the
// inspected page's Runic Inspect API (page.js), which exists only under vite-plugin-runic's development client.
chrome.devtools.panels.create("Runic", "", "panel.html");
chrome.devtools.panels.elements.createSidebarPane("Runic", (pane) => pane.setPage("sidebar.html"));
