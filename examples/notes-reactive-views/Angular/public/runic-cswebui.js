// CS-WebUI owns the browser transport; generated ViewModel modules use this
// small host-neutral Bridge client contract instead of importing CS-WebUI.
window.__runicBridge = {
  isConnected: () => {
    const connected = window.webui?.isConnected() ?? false;
    if (connected) window.webui.allowNavigation?.(true);
    return connected;
  },
  call: (name, ...args) => {
    if (!window.webui) return Promise.reject(new Error("CS-WebUI is unavailable."));
    window.webui.allowNavigation?.(true);
    return window.webui.call(name, ...args);
  }
};
