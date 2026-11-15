// A Bridge for the generated Counter client of examples/first-window, answering as the .NET Window does: the state on
// `open`, and a frame before the reply of each `increment`.
const listeners = new Set();
let revision = 1;
let count = 0;
const state = () => ({ count, step: 1, canIncrement: true });
globalThis.__runicBridge = {
  isConnected: () => true,
  onConnectionChange(report) { report(true); return () => {}; },
  onMessage(next) { listeners.add(next); return () => { listeners.delete(next); }; },
  async request(text) {
    const message = JSON.parse(text);
    const reply = value => JSON.stringify(value);
    switch (message.kind) {
      case "hello": return reply({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features: [] });
      case "open": return reply({ ok: true, presentation: "ok", revision, state: state(), fields: { step: { version: 1 } } });
      case "command": {
        count++;
        revision++;
        const frame = JSON.stringify({ kind: "frame", route: message.route, baseRevision: revision - 1, revision,
          changes: [{ field: "count", kind: "set", value: count }] });
        for (const listener of [...listeners]) listener(frame);
        return reply({ ok: true, revision });
      }
      default: return reply({ ok: true });
    }
  },
};
