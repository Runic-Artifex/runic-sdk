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
      // A Development host lists the development tools' features, trace-context/1 and handler-timing/1, named here
      // without their literal text, which the test looks for in the bundle.
      case "hello":
        globalThis.productionTest.hello = message.features;
        return reply({ ok: true, protocol: 4, implementation: { name: "test", version: "0" },
          features: ["trace-context", "handler-timing"].map(name => `${name}/1`) });
      case "open": return reply({ ok: true, presentation: "ok", revision, state: state(), fields: { step: { version: 1 } } });
      case "command": {
        globalThis.productionTest.commands.push(Object.keys(message).sort());
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
