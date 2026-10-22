// Loads the built package where Symbol.dispose is undefined, as older WebKitGTK and WKWebView
// engines have it, and checks that clients and controllers keep working with dispose().
// Run by lifetime.test.ts; prints "ok".
import assert from "node:assert/strict";

const NativeSymbol = Symbol;
// A copy of Symbol without the two well-known symbols; Symbol's own properties cannot be deleted.
const LegacySymbol = function Symbol(description) { return NativeSymbol(description); };
for (const key of Reflect.ownKeys(NativeSymbol))
  if (!["dispose", "asyncDispose", "length", "name", "prototype"].includes(key)) LegacySymbol[key] = NativeSymbol[key];
globalThis.Symbol = LegacySymbol;
assert.equal(Symbol.dispose, undefined);

const views = await import("../dist/index.js");
const generated = await import("../dist/generated.js");

let released = 0;
const controller = views.createViewController({ release: () => { released++; } });
controller.setSource({ snapshot: 1, subscribe: listener => { listener(1); return () => {}; }, dispose() {} });
const controllers = [
  controller,
  views.createCommandController(() => 1),
  views.createOperationController(async () => undefined),
  views.createLatestOperationController(),
  views.createCollectionViewportController({ totalCount: 1, rowHeight: 1 }),
];
for (const value of controllers) {
  assert.deepEqual(Object.getOwnPropertySymbols(value), []);
  assert.equal("undefined" in value, false);
  value.dispose();
}
let disposed = 0;
const client = generated.disposable({ dispose() { disposed++; } });
assert.deepEqual(Object.getOwnPropertySymbols(client), []);
client.dispose();
assert.equal(disposed, 1);

const aborted = new AbortController();
aborted.abort();
await assert.rejects(views.waitForBridge({ signal: aborted.signal }), error => error.name === "AbortError");
globalThis.Symbol = NativeSymbol;
console.log("ok");
