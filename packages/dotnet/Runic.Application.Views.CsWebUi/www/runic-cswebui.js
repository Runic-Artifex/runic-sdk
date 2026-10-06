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

  // Views frontends own their navigation. Once the bridge connects it cancels
  // navigations, including history.pushState and replaceState, until a Bridge
  // call allows them; a router updating history before its first call would
  // otherwise be cancelled. Allow them before each history update.
  for (const method of ["pushState", "replaceState"]) {
    const update = history[method];
    history[method] = function (...args) {
      window.webui?.allowNavigation?.(true);
      return update.apply(this, args);
    };
  }

  // Native WebUI claims an event slot for each incoming call on its own thread
  // without holding its lock for the whole claim. Two calls arriving together
  // can share one slot, and one of them then never receives a reply (#53).
  // Send the next call only after .NET admitted the previous one, which it
  // reports through __runicBridgeAdmitted, or that call settled. A call .NET
  // never admits holds the others back for at most admissionTimeout.
  const admissionTimeout = 250;
  let admission = Promise.resolve();
  let admitCurrent;
  window.__runicBridgeAdmitted = () => admitCurrent?.();
  function send(name, args) {
    let admitted;
    const previous = admission;
    admission = new Promise(resolve => { admitted = resolve; });
    return previous.then(() => {
      let timer;
      const admit = () => {
        if (admitCurrent === admit) admitCurrent = undefined;
        clearTimeout(timer);
        admitted();
      };
      admitCurrent = admit;
      timer = setTimeout(admit, admissionTimeout);
      let reply;
      try {
        window.webui.allowNavigation?.(true);
        reply = window.webui.call(name, ...args);
      } catch (error) {
        admit();
        throw error;
      }
      Promise.resolve(reply).then(admit, admit);
      return reply;
    });
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
      return send(name, args);
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
