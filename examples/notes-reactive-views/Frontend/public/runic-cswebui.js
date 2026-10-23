// CS-WebUI owns the browser transport; generated ViewModel modules use this
// small host-neutral Bridge client contract instead of importing CS-WebUI.
(() => {
  const connected = () => window.webui?.isConnected() ?? false;
  // A WebSocket reconnect keeps the page. Generated clients then re-read their
  // routes and re-acknowledge mounted Views. Observe the transition by polling
  // so an application's own webui.setEventCallback stays in place. The poll
  // also runs while a call is unsettled, so a lost connection settles it.
  // Connection listeners learn of a dropped WebSocket within one poll, without
  // waiting for the page's next call.
  const reconnectListeners = new Set();
  const connectionListeners = new Set();
  let reconnectTimer;
  let wasConnected = false;
  let lost = false;
  let reported;
  function reportConnection(now) {
    if (now === reported || connectionListeners.size === 0) return;
    reported = now;
    for (const listener of [...connectionListeners]) {
      try { listener(now); } catch (error) { console.error("Runic connection listener failed", error); }
    }
  }
  function connectionLost() {
    lost = true;
    wasConnected = false;
    reportConnection(false);
    const previous = connection;
    connection = nextConnection();
    const error = new Error("The CS-WebUI connection was lost before the call completed.");
    for (const reject of previous.calls) reject(error);
    previous.calls.clear();
  }
  function watchConnection() {
    const now = connected();
    if (!now && wasConnected) connectionLost();
    reportConnection(now);
    wasConnected = now;
    if (now && lost) {
      lost = false;
      for (const listener of [...reconnectListeners]) {
        try { listener(); } catch (error) { console.error("Runic reconnect listener failed", error); }
      }
    }
    if (idle()) stopWatching();
  }
  const idle = () => reconnectListeners.size === 0 && connectionListeners.size === 0 && unsettled === 0;
  function startWatching() {
    if (reconnectTimer !== undefined) return;
    wasConnected = connected();
    reconnectTimer = setInterval(watchConnection, 250);
  }
  function stopWatching() {
    if (reconnectTimer === undefined) return;
    clearInterval(reconnectTimer);
    reconnectTimer = undefined;
    // A loss nobody listened for must not resume a listener registered later.
    lost = false;
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

  // The all-events binding also makes WebUI cancel the page's own navigations
  // from each (re)connect until a Bridge call allows them, such as a reload or
  // a link before the first call, and report each one to .NET (#196). Record
  // the last navigation; when .NET receives the report it calls
  // __runicContinueNavigation, which repeats it with navigation allowed if
  // WebUI cancelled it and it stays on this origin. WebUI registers its
  // listener when the document has loaded, after this one, so the check waits
  // for the report. A cross-origin navigation stays cancelled, and so does a
  // form submission, which cannot be repeated without its data.
  let lastNavigation;
  if (typeof navigation === "object" && navigation !== null && "addEventListener" in navigation) {
    navigation.addEventListener("navigate", event => {
      lastNavigation = event.formData ? undefined
        : { event, url: event.destination.url, type: event.navigationType, key: event.destination.key };
    });
  } else if (typeof document === "object" && typeof document.addEventListener === "function") {
    // Without the Navigation API, WebUI cancels link clicks instead.
    document.addEventListener("click", event => {
      const link = event.target?.closest?.("a[href]");
      lastNavigation = link ? { event, url: link.href, type: "push" } : undefined;
    });
  }
  // .NET calls this through WebUI's raw data (webui_send_raw_client) with one
  // ignored byte, so no script is evaluated.
  window.__runicContinueNavigation = () => {
    const next = lastNavigation;
    lastNavigation = undefined;
    if (!next?.event.defaultPrevented || new URL(next.url, location.href).origin !== location.origin) return;
    // Allow only this navigation. A fragment stays in the document, and WebUI
    // must go on cancelling the page's other navigations until a Bridge call.
    const allow = allowed => window.webui?.allowNavigation?.(allowed);
    allow(true);
    if (next.type === "traverse" && next.key) {
      // A traversal starts after this call returns.
      const { committed, finished } = navigation.traverseTo(next.key);
      finished.catch(() => undefined);
      committed.then(() => allow(false), () => allow(false));
      return;
    }
    try {
      if (next.type === "reload") location.reload();
      else if (next.type === "replace") location.replace(next.url);
      else location.assign(next.url);
    } finally {
      allow(false);
    }
  };

  // .NET publishes Views state as raw data (webui_send_raw): the route, NUL and
  // the state's UTF-8 JSON, which has no raw NUL. Parsing data instead of
  // running a script lets a Content-Security-Policy without 'unsafe-eval'
  // receive state. WebUI evaluates the name when no such function exists, so
  // an older copy of this script misses every state.
  let decoder;
  window.__runicViewsPush = data => {
    const separator = data.lastIndexOf(0);
    if (separator < 0) return;
    decoder ??= new TextDecoder();
    const callback = window[`__${decoder.decode(data.subarray(0, separator))}Changed`];
    if (typeof callback === "function") callback(JSON.parse(decoder.decode(data.subarray(separator + 1))));
  };

  // The window's all-events binding delivers disconnects to .NET, and WebUI has
  // no narrower way to receive them. With it, WebUI also sends a click event for
  // every element with an id. Runic does not use the click events, so they only
  // add traffic next to the Bridge calls each click starts. WebUI skips elements
  // it already marked, so mark each element with an id before WebUI's next scan.
  function suppressClickEvents(node) {
    if (node.nodeType !== 1) return;
    if (node.id) node.dataset.webui_click_is_set = "true";
    for (const element of node.querySelectorAll("[id]")) element.dataset.webui_click_is_set = "true";
  }
  if (typeof MutationObserver === "function" && typeof document === "object") {
    new MutationObserver(records => {
      for (const record of records) {
        if (record.type === "attributes") suppressClickEvents(record.target);
        else for (const node of record.addedNodes) suppressClickEvents(node);
      }
    }).observe(document, { childList: true, subtree: true, attributes: true, attributeFilter: ["id"] });
    if (document.documentElement) suppressClickEvents(document.documentElement);
  }

  // WebUI never settles a call that was in flight when its WebSocket closed,
  // so an awaited reply or long poll would wait forever. Each connection holds
  // the rejections of the calls sent on it until they settle, and the lost
  // transition rejects the rest. A call sent while WebUI reconnects belongs to
  // the next connection.
  const nextConnection = () => ({ calls: new Set() });
  let connection = nextConnection();
  let unsettled = 0;
  function send(name, args) {
    // A throttled poll may not have seen the loss yet. Check now, so this call
    // cannot join the old connection while WebUI sends it on the new one.
    if (wasConnected && !connected()) connectionLost();
    unsettled++;
    startWatching();
    let reply;
    try {
      reply = window.webui.call(name, ...args);
    } catch (error) {
      unsettled--;
      return Promise.reject(error);
    }
    const calls = connection.calls;
    const settled = new Promise((resolve, reject) => {
      calls.add(reject);
      Promise.resolve(reply).then(resolve, reject).finally(() => calls.delete(reject));
    });
    const done = () => { unsettled--; };
    settled.then(done, done);
    return settled;
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
      startWatching();
      return () => {
        reconnectListeners.delete(listener);
        if (idle()) stopWatching();
      };
    },
    // Reads WebUI's state directly: the Bridge's isConnected() also allows navigation.
    onConnectionChange: (listener) => {
      if (connectionListeners.size === 0) reported = connected();
      connectionListeners.add(listener);
      startWatching();
      try { listener(reported); } catch (error) { console.error("Runic connection listener failed", error); }
      return () => {
        connectionListeners.delete(listener);
        if (idle()) stopWatching();
      };
    }
  };
})();
