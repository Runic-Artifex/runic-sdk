// CS-WebUI owns the browser transport; generated ViewModel modules use this
// small host-neutral Bridge client contract instead of importing CS-WebUI.
(() => {
  const connected = () => window.webui?.isConnected() ?? false;
  // A WebSocket reconnect keeps the page. Generated clients then re-read their
  // routes and re-acknowledge mounted Views. Observe the transition by polling
  // so an application's own webui.setEventCallback stays in place. The poll
  // also runs while a call awaits admission, so a lost connection releases it.
  const reconnectListeners = new Set();
  let reconnectTimer;
  let wasConnected = false;
  let lost = false;
  function watchConnection() {
    const now = connected();
    if (!now && wasConnected) {
      lost = true;
      forgetAdmissions();
    }
    wasConnected = now;
    if (now && lost) {
      lost = false;
      for (const listener of [...reconnectListeners]) {
        try { listener(); } catch (error) { console.error("Runic reconnect listener failed", error); }
      }
    }
    if (reconnectListeners.size === 0 && current === undefined && late.length === 0) stopWatching();
  }
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

  // The window's all-events binding delivers disconnects to .NET, and WebUI has
  // no narrower way to receive them. With it, WebUI also sends a click event for
  // every element with an id, in parallel with the Bridge calls that click
  // starts, and those can collide in native WebUI (#53). Runic does not use the
  // click events. WebUI skips elements it already marked, so mark each element
  // with an id before WebUI's next scan.
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

  // Native WebUI claims an event slot for each incoming call on its own thread
  // without holding its lock for the whole claim. Two calls arriving together
  // can share one slot: one of them then never receives a reply, and either
  // can read the other's arguments or response (#53). Send the next call only
  // after .NET admitted the previous one, which it reports through
  // __runicBridgeAdmitted, or that call settled. A call that reaches no handler
  // and never settles holds the others back for at most admissionTimeout.
  const admissionTimeout = 1500;
  let admission = Promise.resolve();
  let current;
  // Calls the timeout released before .NET admitted them. Their admissions can
  // still arrive and must not release a later call before it has a slot.
  const late = [];
  window.__runicBridgeAdmitted = () => {
    if (late.length !== 0) late.shift();
    else current?.admit();
  };
  // WebUI never settles calls that were in flight when its WebSocket closed,
  // and .NET cannot admit them over the new connection. Release the waiting
  // call and drop the late ones, so their missing admissions cannot hold back
  // or release calls sent after the reconnect.
  function forgetAdmissions() {
    late.length = 0;
    current?.admit();
  }
  // Smoke diagnostics; not part of the Bridge client contract.
  window.__runicBridgeAdmissionState = () => ({ late: late.length, waiting: current !== undefined });
  function send(name, args) {
    let release;
    const previous = admission;
    admission = new Promise(resolve => { release = resolve; });
    return previous.then(() => {
      let timer;
      const call = {
        admit() {
          if (current !== call) return;
          current = undefined;
          clearTimeout(timer);
          release();
        },
        settle() {
          if (current === call) call.admit();
          // An admission precedes its reply, so a late call that settles was
          // never admitted.
          const index = late.indexOf(call);
          if (index !== -1) late.splice(index, 1);
        },
      };
      current = call;
      startWatching();
      timer = setTimeout(() => {
        if (current !== call) return;
        current = undefined;
        late.push(call);
        release();
      }, admissionTimeout);
      let reply;
      try {
        reply = window.webui.call(name, ...args);
      } catch (error) {
        call.settle();
        throw error;
      }
      Promise.resolve(reply).then(call.settle, call.settle);
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
      startWatching();
      return () => {
        reconnectListeners.delete(listener);
        if (reconnectListeners.size === 0 && current === undefined && late.length === 0) stopWatching();
      };
    }
  };
})();
