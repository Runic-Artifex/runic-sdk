// The Desktop surface serves webui.js; Views clients consume this small,
// host-neutral Bridge client (Views protocol 4, specs/application/README.md,
// "Host bridge (page)") instead of the Desktop transport directly.
(() => {
  // webui.js installs __runicDesktopViews before this script runs: the Views
  // messages of its reserved raw-data function, in order, and the connection's
  // transitions, reported from the WebSocket itself (true once its token is
  // accepted, false when it closes) without taking the application's
  // webui.setEventCallback. Both are read when used.
  const views = () => globalThis.__runicDesktopViews;
  const connected = () => views()?.isConnected() ?? globalThis.webui?.isConnected() ?? false;

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

  globalThis.__runicBridge = {
    isConnected: connected,
    request: message => {
      if (!globalThis.webui) return Promise.reject(new Error("Runic Desktop is unavailable."));
      globalThis.webui.allowNavigation?.(true);
      return globalThis.webui.call("__runicViews", message);
    },
    onMessage: listener => views()?.onMessage(listener) ?? (() => {}),
    // The current state at once, then each transition. A listener never gets
    // the same value twice in a row, so each true after false is a new
    // connection.
    onConnectionChange: listener => {
      let reported;
      let active = true;
      const report = now => {
        if (!active || now === reported) return;
        reported = now;
        try { listener(now); } catch (error) { console.error("Runic connection listener failed", error); }
      };
      report(connected());
      const stop = views()?.onConnectionChange(report);
      return () => {
        active = false;
        stop?.();
      };
    },
    call: legacyCall
  };
})();
