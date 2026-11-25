// view-contract/1 (specs/application/README.md, "Presentations"; sdk#1324): the development build lists the feature,
// and a producer that uses it names the route's generated contract in each `open` reply. A page whose generated client
// has another contract fails to connect with one `contract` diagnostic, before it decodes the state. A Bridge before
// 0.24 names no contract, and the production build neither lists the feature nor checks a contract it receives.
import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import type { BridgeDiagnostic, RunicBridgeClient } from "../dist/index.js";
import * as production from "../dist/generated.js";
import { development, developmentGenerated, developmentWire as bridgeWire } from "./support/development.js";

const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
const host = globalThis as unknown as Record<string | symbol, unknown>;
const pageContract = "Demo.EditorViewModel:PAGE1";
type State = { readonly title: string };
const members = { title: bridgeWire.string };

function reset(): void {
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  delete host["__runicBridge"];
}

afterEach(reset);

/**
 * A host bridge that lists view-contract/1 and, when the page's hello lists it too, names `bridgeContract` in each
 * `open` reply, as a .NET host of 0.24 does. `bridgeContract: undefined` is a producer before 0.24.
 */
function installBridge(bridgeContract: string | undefined) {
  const requests: Record<string, unknown>[] = [];
  let uses = false;
  const bridge: RunicBridgeClient = {
    isConnected: () => true,
    onConnectionChange(report) { report(true); return () => {}; },
    onMessage() { return () => {}; },
    async request(text) {
      const message = JSON.parse(text) as Record<string, unknown>;
      requests.push(message);
      const kind = message["kind"];
      if (kind === "hello") {
        uses = bridgeContract !== undefined && (message["features"] as string[]).includes("view-contract/1");
        const features = bridgeContract === undefined ? ["request-session/1"] : ["request-session/1", "view-contract/1"];
        return JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features });
      }
      if (kind === "open")
        return JSON.stringify({ ok: true, presentation: "ok", revision: 1, state: { title: "a" }, ...uses ? { contract: bridgeContract } : {} });
      return JSON.stringify({ ok: true, revision: 1 });
    },
  };
  host["__runicBridge"] = bridge;
  return {
    requests,
    hello: () => requests.find(request => request["kind"] === "hello")!,
    kinds: () => requests.map(request => request["kind"]),
  };
}

function diagnosticsOf(build: { onBridgeDiagnostic(listener: (diagnostic: BridgeDiagnostic) => void): () => void }) {
  const diagnostics: BridgeDiagnostic[] = [];
  const stop = build.onBridgeDiagnostic(diagnostic => { diagnostics.push(diagnostic); });
  return { diagnostics, stop };
}

const connect = (generated: typeof production, contract = pageContract) =>
  generated.connectView<State>({ contract, route: "editor", members });

test("a page whose contract is the Bridge's connects as before", async () => {
  const bridge = installBridge(pageContract);
  const { diagnostics, stop } = diagnosticsOf(development);
  try {
    const client = await connect(developmentGenerated);
    assert.ok((bridge.hello()["features"] as string[]).includes("view-contract/1"), "the development build lists the feature");
    assert.equal(client.snapshot.title, "a");
    assert.deepEqual(diagnostics, []);
    client.dispose();
  } finally {
    stop();
  }
});

test("a page generated from another build of its ViewModel fails to connect with one contract diagnostic", async () => {
  const bridge = installBridge("Demo.EditorViewModel:APP22");
  const { diagnostics, stop } = diagnosticsOf(development);
  try {
    await assert.rejects(connect(developmentGenerated), (error: unknown) => {
      assert.ok(error instanceof development.BridgeError);
      assert.equal(error.kind, "failed");
      assert.equal(error.route, "editor");
      assert.match(error.message, /page and the application were generated from different builds of the View EditorViewModel/);
      assert.match(error.message, /PAGE1.*APP22/);
      return true;
    });
    assert.equal(diagnostics.length, 1, `one diagnostic, not ${JSON.stringify(diagnostics.map(diagnostic => diagnostic.code))}`);
    const [diagnostic] = diagnostics;
    assert.equal(diagnostic!.kind, "error");
    assert.equal(diagnostic!.code, "contract");
    assert.equal(diagnostic!.route, "editor");
    assert.match(diagnostic!.message, /EditorViewModel/);
    assert.match(diagnostic!.message, /page: Demo\.EditorViewModel:PAGE1; application: Demo\.EditorViewModel:APP22/);
    // The producer registered the presentation; the page closes it.
    for (let wait = 0; !bridge.kinds().includes("close") && wait < 100; wait++) await new Promise(resolve => setTimeout(resolve, 1));
    assert.deepEqual(bridge.kinds().filter(kind => kind === "open" || kind === "close"), ["open", "close"]);
  } finally {
    stop();
  }
});

test("a Bridge before 0.24, which names no contract, does not trip the check", async () => {
  installBridge(undefined);
  const { diagnostics, stop } = diagnosticsOf(development);
  try {
    const client = await connect(developmentGenerated);
    assert.equal(client.snapshot.title, "a");
    assert.deepEqual(diagnostics, []);
    client.dispose();
  } finally {
    stop();
  }
});

test("the production build lists no view-contract/1 and does not compare contracts", async () => {
  const bridge = installBridge("Demo.EditorViewModel:APP22");
  // A producer that sent a contract anyway: the production build ignores the member.
  const request = (host["__runicBridge"] as RunicBridgeClient).request;
  (host["__runicBridge"] as { request: RunicBridgeClient["request"] }).request = async text => {
    const reply = await request(text);
    return (JSON.parse(text) as { kind: string }).kind !== "open" ? reply
      : JSON.stringify({ ...JSON.parse(reply) as object, contract: "Demo.EditorViewModel:APP22" });
  };
  const { diagnostics, stop } = diagnosticsOf(await import("../dist/index.js"));
  try {
    const client = await connect(production);
    assert.ok(!(bridge.hello()["features"] as string[]).includes("view-contract/1"));
    assert.equal(client.snapshot.title, "a");
    assert.deepEqual(diagnostics, []);
    client.dispose();
  } finally {
    stop();
  }
});
