// CS-WebUI owns the browser transport; generated ViewModel modules use this
// small host-neutral Bridge client (Views protocol 4, specs/application/README.md,
// "Host bridge (page)") instead of importing CS-WebUI.
(() => {
  const connected = () => window.webui?.isConnected() ?? false;
  // A WebSocket reconnect keeps the page, and each new connection needs its own
  // hello. WebUI reports the transitions to its one event callback
  // (webui.setEventCallback): CONNECTED once a connection's token is accepted,
  // which is when isConnected() turns true, and DISCONNECTED when its WebSocket
  // closes. webui.js defines the WebuiBridge class and creates its one
  // instance, window.webui, when the document has loaded, after this script
  // ran. So the script wraps WebuiBridge.prototype.setEventCallback before any
  // page script can call it, and once the Bridge starts watching it installs
  // its own callback, which observes the transition and then calls the
  // application's callback. An application's own callback therefore stays in
  // place and sees every event WebUI reports, before and after. Like an
  // application's callback, the Bridge's makes WebUI leave out its own
  // "connection lost" banner. Where WebUI cannot be observed so (this script
  // loaded after window.webui was created, or a WebUI without the class), the
  // script polls isConnected() every 250 ms instead, as it did before.
  // Watching starts when a listener registers or a call is sent, so a lost
  // connection settles the call, and connection listeners learn of a dropped
  // WebSocket at once, without waiting for the page's next call. A poll stops
  // once nothing listens and no call is unsettled; the callback stays.
  const connectionListeners = new Set();
  const messageListeners = new Set();
  let watchTimer;
  let wasConnected = false;
  let reported;
  const WebuiBridge = typeof window.WebuiBridge === "function" && !("webui" in window) ? window.WebuiBridge : undefined;
  const setEventCallback = WebuiBridge?.prototype.setEventCallback;
  // Whether WebUI reports the transitions, and the instance that reports them to the Bridge's callback.
  let events = typeof setEventCallback === "function";
  let reporting;
  let applicationCallback = null;
  if (events) {
    WebuiBridge.prototype.setEventCallback = function (callback) {
      applicationCallback = callback;
      if (this !== reporting) setEventCallback.call(this, callback);
    };
  }
  function bridgeCallback(event) {
    try {
      // CONNECTED while the last connection was open: WebUI did not report its
      // close to this callback, so the page has a new connection.
      if (event === (reporting.event?.CONNECTED ?? 0) && wasConnected) connectionLost();
      watchConnection();
    } catch (error) {
      console.error("Runic connection event failed", error);
    }
    if (typeof applicationCallback === "function") applicationCallback.call(this, event);
  }
  function reportEvents() {
    const webui = window.webui;
    if (reporting !== undefined || webui === undefined) return;
    if (!(webui instanceof WebuiBridge)) {
      events = false;
      return;
    }
    wasConnected = connected();
    setEventCallback.call(webui, bridgeCallback);
    reporting = webui;
  }
  function reportConnection(now) {
    if (now === reported || connectionListeners.size === 0) return;
    reported = now;
    for (const listener of [...connectionListeners]) {
      try { listener(now); } catch (error) { console.error("Runic connection listener failed", error); }
    }
  }
  function connectionLost() {
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
    const opened = now && !wasConnected;
    wasConnected = now;
    if (opened) receive();
    if (idle()) stopWatching();
  }
  const idle = () => connectionListeners.size === 0 && messageListeners.size === 0 && unsettled === 0;
  function startWatching() {
    if (events) {
      // Before window.webui exists, nothing can connect; the document's load
      // installs the callback once webui.js created it.
      reportEvents();
      if (events) return;
    }
    if (watchTimer !== undefined) return;
    wasConnected = connected();
    watchTimer = setInterval(watchConnection, 250);
  }
  // webui.js registered its listener before this one, so window.webui exists here.
  if (events && typeof document === "object" && typeof document.addEventListener === "function") {
    document.addEventListener("DOMContentLoaded", () => {
      if (idle()) return;
      startWatching();
      watchConnection();
    });
  }
  function stopWatching() {
    if (watchTimer === undefined) return;
    clearInterval(watchTimer);
    watchTimer = undefined;
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

  function deliver(message) {
    for (const listener of [...messageListeners]) {
      try { listener(message); } catch (error) { console.error("Runic message listener failed", error); }
    }
  }

  // .NET broadcasts Views pushes as raw data (webui_send_raw): the message's
  // UTF-8 JSON. Decoding data instead of running a script lets a
  // Content-Security-Policy without 'unsafe-eval' receive them. WebUI
  // evaluates the name when no such function exists, so an older copy of this
  // script misses every push.
  let decoder;
  window.__runicViewsPush = data => {
    if (messageListeners.size === 0) return;
    decoder ??= new TextDecoder();
    deliver(decoder.decode(data));
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
  // so an awaited reply or a receive would wait forever. Each connection holds
  // the rejections of the calls sent on it until they settle, and the lost
  // transition rejects the rest. A call sent while WebUI reconnects belongs to
  // the next connection. A connection also holds the state of its receive loop.
  const nextConnection = () => ({ calls: new Set(), settled: 0, receiving: false, paused: false, received: false });
  let connection = nextConnection();
  let unsettled = 0;
  // The connection a call sent now joins. The script may not have seen the
  // loss yet (a throttled poll, or a WebSocket that stopped being open before
  // WebUI reported its close). Check now, so a call cannot join the old
  // connection while WebUI sends it on the new one.
  function currentConnection() {
    if (wasConnected && !connected()) connectionLost();
    return connection;
  }
  function send(joined, name, args) {
    const calls = joined.calls;
    unsettled++;
    startWatching();
    let reply;
    try {
      reply = window.webui.call(name, ...args);
    } catch (error) {
      unsettled--;
      return Promise.reject(error);
    }
    const settled = new Promise((resolve, reject) => {
      calls.add(reject);
      Promise.resolve(reply).then(resolve, reject).finally(() => calls.delete(reject));
    });
    const done = () => { unsettled--; };
    settled.then(done, done);
    return settled;
  }

  // The mailbox (specs/application/README.md, "receive"). WebUI cannot send to
  // one client outside a call, so .NET queues a connection's targeted pushes
  // (operations, interactions) and this loop drains them: while a message
  // listener exists and the connection is open, exactly one receive request is
  // pending on it. Its reply carries the queued messages, as soon as one is
  // queued or empty after 25 seconds, and the next receive is sent at once.
  // The pending receive does not hold up the page's other calls: WebUI runs
  // each call on its own native thread and assigns each its own event slot.
  //
  // A receive fails with no-hello until the page's hello on this connection
  // has completed; the page's runtime sends it through request(). Any failed or
  // unreadable reply pauses the loop until a request() sent on the same
  // connection settles after the failed receive was sent, so the loop never
  // spins. If such a request already settled when the failure arrives (a
  // receive racing the hello), the loop sends the next receive at once. A
  // no-hello after this connection's loop received a reply means that WebUI
  // replaced the WebSocket without the script seeing it close: the page has a
  // new connection, so its listeners see false and then true, and its
  // unsettled calls, which WebUI will not settle, are rejected.
  // A receive rejected because its connection was lost ends that connection's
  // loop; the next connection starts a new one when the script sees it open or
  // when a request settles on it. A receive rejected otherwise is a failure.
  const receiveRequest = '{"kind":"receive"}';
  function receive() {
    if (messageListeners.size === 0 || !window.webui || !connected()) return;
    const loop = currentConnection();
    if (loop.receiving || loop.paused) return;
    loop.receiving = true;
    const since = loop.settled;
    const failed = () => {
      if (loop.settled !== since) receive();
      else loop.paused = true;
    };
    send(loop, "__runicViews", [receiveRequest]).then(text => {
      loop.receiving = false;
      if (loop !== connection) return;
      let reply;
      try { reply = JSON.parse(text); } catch { reply = undefined; }
      if (reply?.ok === true && Array.isArray(reply.messages)) {
        loop.received = true;
        // Messages that arrive while no listener exists are dropped.
        for (const message of reply.messages) deliver(JSON.stringify(message));
        receive();
      } else if (reply?.ok === false && reply.error?.reason === "no-hello" && loop.received) {
        connectionLost();
        connection.paused = true;
        if (connected()) {
          wasConnected = true;
          reportConnection(true);
        }
      } else {
        failed();
      }
    }, () => {
      loop.receiving = false;
      if (loop === connection) failed();
    });
  }
  function requestSettled(joined) {
    joined.settled++;
    if (joined !== connection) return;
    joined.paused = false;
    receive();
  }

  // A page of protocol 3 calls named functions instead; it gets a reply that
  // makes it report its protocol diagnostic, and nothing reaches the host.
  const releasedReply = '{"ok":false,"state":null,"error":{"kind":"released","message":"This View was released."}}';
  const snapshotReply = '{"ok":true,"state":{"revision":0},"error":null,"protocol":4}';
  // A protocol-3 page, or a wrapper written for it, calls call(name) and sees only released Views: say why, once.
  let legacyCallReported = false;
  const legacyCall = name => {
    if (!legacyCallReported) {
      legacyCallReported = true;
      console.warn(`Runic: this page called __runicBridge.call(${JSON.stringify(String(name))}), the Views protocol 3 API. ` +
        "This host speaks Views protocol 4 and answers call() only with a released View, so nothing reaches .NET. " +
        "Rebuild the frontend with the matching @runic-artifex/views and reload; code that calls the host bridge itself " +
        "sends protocol 4 messages with __runicBridge.request(message).");
    }
    return Promise.resolve(typeof name === "string" && name.endsWith("Snapshot") ? snapshotReply : releasedReply);
  };

  window.__runicBridge = {
    isConnected: () => {
      const isConnected = connected();
      if (isConnected) window.webui.allowNavigation?.(true);
      return isConnected;
    },
    request: message => {
      if (!window.webui) return Promise.reject(new Error("CS-WebUI is unavailable."));
      window.webui.allowNavigation?.(true);
      const joined = currentConnection();
      const reply = send(joined, "__runicViews", [message]);
      const settled = () => requestSettled(joined);
      reply.then(settled, settled);
      return reply;
    },
    onMessage: listener => {
      // Each subscription is its own entry, even for a listener added twice.
      const subscription = message => listener(message);
      messageListeners.add(subscription);
      startWatching();
      receive();
      return () => {
        messageListeners.delete(subscription);
        if (idle()) stopWatching();
      };
    },
    // Reads WebUI's state directly: the Bridge's isConnected() also allows navigation.
    onConnectionChange: listener => {
      // A later listener gets the current state, not the last one reported: WebUI can have connected since.
      if (connectionListeners.size === 0) reported = connected();
      else watchConnection();
      connectionListeners.add(listener);
      startWatching();
      try { listener(reported); } catch (error) { console.error("Runic connection listener failed", error); }
      return () => {
        connectionListeners.delete(listener);
        if (idle()) stopWatching();
      };
    },
    call: legacyCall
  };
})();
