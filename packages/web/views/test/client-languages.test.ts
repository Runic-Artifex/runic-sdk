// client-languages/1 on the client (specs/application/README.md, "Client languages"): every hello lists the feature
// and sends the browser's languages.
import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import { connectView } from "../dist/generated.js";
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
