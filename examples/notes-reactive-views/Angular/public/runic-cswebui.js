// CS-WebUI owns the browser transport; generated ViewModel modules use this
// small host-neutral Bridge client contract instead of importing CS-WebUI.
(() => {
  const connected = () => window.webui?.isConnected() ?? false;
  // A WebSocket reconnect keeps the page. Generated clients then re-read their
  // routes and re-acknowledge mounted Views. Observe the transition by polling
  // so an application's own webui.setEventCallback stays in place.
  const reconnectListeners = new Set();
  let reconnectTimer;
  let wasConnected = false;
  let lost = false;
  function watchConnection() {
    const now = connected();
    if (!now && wasConnected) lost = true;
    wasConnected = now;
    if (!now || !lost) return;
    lost = false;
    for (const listener of [...reconnectListeners]) {
      try { listener(); } catch (error) { console.error("Runic reconnect listener failed", error); }
    }
  }

  window.__runicBridge = {
    isConnected: () => {
      const isConnected = connected();
      if (isConnected) window.webui.allowNavigation?.(true);
      return isConnected;
    },
    call: (name, ...args) => {
      if (!window.webui) return Promise.reject(new Error("CS-WebUI is unavailable."));
      window.webui.allowNavigation?.(true);
      return window.webui.call(name, ...args);
    },
    onReconnect: (listener) => {
      reconnectListeners.add(listener);
      if (reconnectTimer === undefined) {
        wasConnected = connected();
        reconnectTimer = setInterval(watchConnection, 250);
      }
      return () => {
        reconnectListeners.delete(listener);
        if (reconnectListeners.size !== 0 || reconnectTimer === undefined) return;
        clearInterval(reconnectTimer);
        reconnectTimer = undefined;
      };
    }
  };
})();
