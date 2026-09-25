// The Desktop surface serves webui.js; Views clients consume this small,
// host-neutral call interface instead of the Desktop transport directly.
globalThis.__runicBridge = {
  isConnected: () => globalThis.webui?.isConnected() ?? false,
  call: (name, ...args) => {
    if (!globalThis.webui) return Promise.reject(new Error("Runic Desktop is unavailable."));
    globalThis.webui.allowNavigation?.(true);
    return globalThis.webui.call(name, ...args);
  }
};
