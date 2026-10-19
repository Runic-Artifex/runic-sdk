// The Desktop surface serves webui.js; Views clients consume this small,
// host-neutral call interface instead of the Desktop transport directly.
(() => {
  const connected = () => globalThis.webui?.isConnected() ?? false;
  // The Desktop bridge reconnects its WebSocket without reloading the page.
  // Generated clients then re-read their routes and re-acknowledge mounted
  // Views. Observe the transition by polling so an application's own
  // webui.setEventCallback stays in place. Connection listeners learn of a
  // dropped WebSocket within one poll, without waiting for the page's next call.
  const reconnectListeners = new Set();
  const connectionListeners = new Set();
  let reconnectTimer;
  let wasConnected = false;
  let lost = false;
  let reported;
  function reportConnection(now) {
    if (now === reported) return;
    reported = now;
    for (const listener of [...connectionListeners]) {
      try { listener(now); } catch (error) { console.error("Runic connection listener failed", error); }
    }
  }
  function watchConnection() {
    const now = connected();
    reportConnection(now);
    if (!now && wasConnected) lost = true;
    wasConnected = now;
    if (!now || !lost) return;
    lost = false;
    for (const listener of [...reconnectListeners]) {
      try { listener(); } catch (error) { console.error("Runic reconnect listener failed", error); }
    }
  }
  function startWatching() {
    if (reconnectTimer !== undefined) return;
    wasConnected = connected();
    reconnectTimer = setInterval(watchConnection, 250);
  }
  function stopWatchingWhenIdle() {
    if (reconnectListeners.size !== 0 || connectionListeners.size !== 0 || reconnectTimer === undefined) return;
    clearInterval(reconnectTimer);
    reconnectTimer = undefined;
  }

  // Views frontends own their navigation. Once the bridge connects it cancels
  // navigations, including history.pushState and replaceState, until a Bridge
  // call allows them; a router updating history before its first call would
  // otherwise be cancelled. Allow them before each history update.
  for (const method of ["pushState", "replaceState"]) {
    const update = history[method];
    history[method] = function (...args) {
      globalThis.webui?.allowNavigation?.(true);
      return update.apply(this, args);
    };
  }

  globalThis.__runicBridge = {
    isConnected: connected,
    call: (name, ...args) => {
      if (!globalThis.webui) return Promise.reject(new Error("Runic Desktop is unavailable."));
      globalThis.webui.allowNavigation?.(true);
      return globalThis.webui.call(name, ...args);
    },
    onReconnect: (listener) => {
      reconnectListeners.add(listener);
      startWatching();
      return () => {
        reconnectListeners.delete(listener);
        stopWatchingWhenIdle();
      };
    },
    onConnectionChange: (listener) => {
      if (connectionListeners.size === 0) reported = connected();
      connectionListeners.add(listener);
      startWatching();
      try { listener(reported); } catch (error) { console.error("Runic connection listener failed", error); }
      return () => {
        connectionListeners.delete(listener);
        stopWatchingWhenIdle();
      };
    }
  };
})();
