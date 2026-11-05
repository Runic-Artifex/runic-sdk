// client-languages/1 on the client (specs/application/README.md, "Client languages"): every hello lists the feature
// and sends the browser's languages.
import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import { connectView } from "../dist/generated.js";
import { configureClientLanguages } from "../dist/index.js";
import { createMockBridge, installMockBridge } from "../dist/mock.js";
import * as bridgeWire from "../dist/wire.js";

const original = Object.getOwnPropertyDescriptor(globalThis, "navigator");
afterEach(() => {
  if (original) Object.defineProperty(globalThis, "navigator", original);
  else delete (globalThis as { navigator?: unknown }).navigator;
});

function browser(navigator: { languages?: unknown; language?: unknown }) {
  Object.defineProperty(globalThis, "navigator", { configurable: true, writable: true, value: navigator });
}

test("the hello sends navigator.languages: tags in order, each once, at most 16", async () => {
  browser({ languages: ["de-CH", "de", "DE", "en_US", "", 5, ...Array.from({ length: 20 }, (_, index) => `x${index}`)], language: "fr" });
  const bridge = createMockBridge();
  installMockBridge(bridge);
  bridge.view("status", { state: { ready: true } });
  await connectView<{ readonly ready: boolean }>({ contract: "Tests.Status:fp-1", route: "status", members: { ready: bridgeWire.boolean } });
  const hello = bridge.calls.find(call => call.kind === "hello");
  assert.ok(hello);
  const message = hello.message as { features: string[]; languages: string[] };
  assert.ok(message.features.includes("client-languages/1"));
  assert.deepEqual(message.languages, ["de-CH", "de", ...Array.from({ length: 14 }, (_, index) => `x${index}`)]);
  assert.deepEqual(bridge.clientLanguages, message.languages);
});

test("configureClientLanguages({ send: false }) sends no languages but keeps the feature", async () => {
  browser({ languages: ["de-CH", "de"] });
  const restore = configureClientLanguages({ send: false });
  try {
    const bridge = createMockBridge();
    installMockBridge(bridge);
    bridge.view("status", { state: { ready: true } });
    await connectView<{ readonly ready: boolean }>({ contract: "Tests.Status:fp-1", route: "status", members: { ready: bridgeWire.boolean } });
    const hello = bridge.calls.find(call => call.kind === "hello");
    assert.ok(hello);
    const message = hello.message as { features: string[]; languages: string[] };
    assert.ok(message.features.includes("client-languages/1"));
    assert.deepEqual(message.languages, []);
    assert.deepEqual(bridge.clientLanguages, []);
  } finally {
    restore();
  }
});

test("the mock bridge keeps the language tags as a producer does: BCP 47 case, each once, long tags ignored", async () => {
  browser({ languages: [] });
  const bridge = createMockBridge();
  installMockBridge(bridge);
  const reply = JSON.parse(await bridge.request(JSON.stringify({
    kind: "hello", protocol: 4, session: "s-1", client: { name: "test", version: "1.0.0" },
    features: ["client-languages/1"], languages: ["DE-ch", "de-CH", "zh-hant-tw", "ES-419", "a" + "b".repeat(64), "en-US-U-CA-Buddhist"],
  }))) as { ok: boolean };
  assert.equal(reply.ok, true);
  assert.deepEqual(bridge.clientLanguages, ["de-CH", "zh-Hant-TW", "es-419", "en-US-u-ca-buddhist"]);
});
