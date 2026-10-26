// Views protocol 3 (specs/application/README.md, fixtures/released and fixtures/error-reasons):
// .NET answers a released View `released`, a rejected reply may carry an open `reason`, and a
// client refuses a host of another protocol version. The .NET tests write the
// Runic.Application.Views fixtures; this client reads every fixture, whatever its producer.
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";
import { BridgeError, onBridgeDiagnostic, type BridgeDiagnostic } from "../dist/index.js";
import { bridgeOperations, connectView } from "../dist/generated.js";
import { createMockBridge, installMockBridge, type MockBridge } from "../dist/mock.js";

interface Fixture {
  readonly description: string;
  readonly scenario: string;
  readonly producer: "Runic.Application.Views" | "host" | "protocol-2";
  readonly route: "snapshot" | "command" | "write" | "mount";
  readonly wire: unknown;
  readonly client: {
    readonly error: { readonly kind: string; readonly reason?: string; readonly message?: string };
    readonly diagnostics: readonly string[];
  };
}

const contract = "Tests.Page:fixture";
const hydrate = (wire: Record<string, unknown>) => ({ ...wire });
const runtimeKey = Symbol.for("runic.views.generated-client-runtime");

function fresh(): MockBridge {
  delete (globalThis as unknown as Record<symbol, unknown>)[runtimeKey];
  const bridge = installMockBridge(createMockBridge());
  bridge.view("page", { state: {} });
  return bridge;
}

// Answers the fixture's wire on its route and returns the call that observes it.
async function observe(bridge: MockBridge, fixture: Fixture): Promise<unknown> {
  const wire = typeof fixture.wire === "string" ? fixture.wire : JSON.stringify(fixture.wire);
  switch (fixture.route) {
    case "snapshot":
      bridge.route("pageSnapshot", () => wire);
      return connectView({ contract, route: "page", mount: false, hydrate });
    case "mount":
      bridge.route("pageMount", () => wire);
      return connectView({ contract, route: "page", mount: true, hydrate });
    case "command": {
      const client = await connectView({ contract, route: "page", mount: false, hydrate, operations: bridgeOperations });
      bridge.route("pageSave", () => wire);
      try { return await client.command("pageSave"); } finally { client.dispose(); }
    }
    case "write": {
      const client = await connectView({ contract, route: "page", mount: false, hydrate, checkedFields: ["title"] });
      bridge.route("pageWriteTitle", () => wire);
      try { return await client.writeField("pageWriteTitle", "{}", value => value); } finally { client.dispose(); }
    }
  }
}

for (const folder of ["released", "error-reasons"]) {
  const directory = new URL(`../../../../specs/application/fixtures/${folder}/`, import.meta.url);
  const files = readdirSync(directory).filter(file => file.endsWith(".json")).sort();
  test(`${folder} fixtures exist`, () => assert.ok(files.length > 0));
  for (const file of files) {
    const fixture = JSON.parse(readFileSync(new URL(file, directory), "utf8")) as Fixture;
    test(`${folder}/${file}: ${fixture.description}`, async () => {
      const bridge = fresh();
      const diagnostics: BridgeDiagnostic[] = [];
      const stop = onBridgeDiagnostic(diagnostic => diagnostics.push(diagnostic));
      try {
        const expected = fixture.client.error;
        await assert.rejects(observe(bridge, fixture), (error: unknown) => {
          assert.ok(error instanceof BridgeError, `rejected with ${String(error)}`);
          assert.equal(error.kind, expected.kind);
          assert.equal(error.reason, expected.reason);
          if (expected.message !== undefined) assert.ok(error.message.includes(expected.message), error.message);
          return true;
        });
        assert.deepEqual(diagnostics.map(diagnostic => diagnostic.code), fixture.client.diagnostics);
      } finally {
        stop();
      }
    });
  }
}

test("the mock speaks protocol 3 and answers released everywhere .NET does", async () => {
  const bridge = fresh();
  const snapshot = JSON.parse(await bridge.call("pageSnapshot")) as Record<string, unknown>;
  assert.equal(snapshot["protocol"], 3);
  // An interaction poll of a presentation that does not exist, as after its View was released.
  const wait = JSON.parse(await bridge.call("__runicInteractionWait", JSON.stringify({ route: "page", presentationId: "gone:1", handlers: [] })));
  assert.deepEqual(wait, { kind: "released" });
  const control = JSON.parse(await bridge.call("__runicInteractionControlWait", JSON.stringify({ route: "page", presentationId: "gone:1" })));
  assert.deepEqual(control, { kind: "released" });
});

// Issue #642: only a snapshot read checks the protocol. A command or setter whose
// member name ends in "Snapshot" gets an ordinary reply, which never states one.
async function withDiagnostics<T>(run: (diagnostics: BridgeDiagnostic[]) => Promise<T>): Promise<T> {
  const diagnostics: BridgeDiagnostic[] = [];
  const stop = onBridgeDiagnostic(diagnostic => diagnostics.push(diagnostic));
  try { return await run(diagnostics); } finally { stop(); }
}

test("a command named CreateSnapshot succeeds although its reply states no protocol", async () => {
  const bridge = fresh();
  await withDiagnostics(async diagnostics => {
    const client = await connectView({ contract, route: "page", mount: false, hydrate, operations: bridgeOperations });
    try {
      bridge.route("pageCreateSnapshot", () => JSON.stringify({ ok: true, state: { revision: 2, title: "created" }, error: null }));
      assert.deepEqual(await client.command("pageCreateSnapshot"), { revision: 2, title: "created" });
      const outcome = await client.commandOutcome("pageCreateSnapshot", value => value);
      assert.ok(outcome.ok);
      assert.deepEqual(outcome.value, { revision: 2, title: "created" });
      assert.deepEqual(diagnostics.map(diagnostic => diagnostic.code), []);
    } finally { client.dispose(); }
  });
});

test("a command named CreateSnapshot that released its View resolves with the last state", async () => {
  const bridge = fresh();
  await withDiagnostics(async diagnostics => {
    const client = await connectView({ contract, route: "page", mount: false, hydrate, operations: bridgeOperations });
    try {
      const before = client.snapshot;
      bridge.route("pageCreateSnapshot", () => JSON.stringify({ ok: true, state: null, error: null, released: true }));
      assert.equal(await client.command("pageCreateSnapshot"), before);
      assert.deepEqual(diagnostics.map(diagnostic => diagnostic.code), []);
    } finally { client.dispose(); }
  });
});

test("the setter of a property named LastSnapshot succeeds although its reply states no protocol", async () => {
  const bridge = fresh();
  await withDiagnostics(async diagnostics => {
    const client = await connectView({ contract, route: "page", mount: false, hydrate });
    try {
      bridge.route("pageSetLastSnapshot", (value: unknown) => JSON.stringify({ ok: true, state: { revision: 2, lastSnapshot: value }, error: null }));
      assert.deepEqual(await client.invoke("pageSetLastSnapshot", "s1"), { revision: 2, lastSnapshot: "s1" });
      assert.deepEqual(diagnostics.map(diagnostic => diagnostic.code), []);
    } finally { client.dispose(); }
  });
});

test("a snapshot read without a protocol still fails with the protocol diagnostic", async () => {
  const bridge = fresh();
  await withDiagnostics(async diagnostics => {
    bridge.route("pageSnapshot", () => JSON.stringify({ ok: true, state: {}, error: null }));
    await assert.rejects(connectView({ contract, route: "page", mount: false, hydrate }), (error: unknown) =>
      error instanceof BridgeError && error.kind === "failed" && error.message.includes("does not state a Views protocol"));
    assert.deepEqual(diagnostics.map(diagnostic => [diagnostic.code, diagnostic.route]), [["protocol", "pageSnapshot"]]);
  });
});

test("a snapshot read with another protocol still fails with the protocol diagnostic", async () => {
  const bridge = fresh();
  await withDiagnostics(async diagnostics => {
    bridge.route("pageSnapshot", () => JSON.stringify({ ok: true, state: {}, error: null, protocol: 4 }));
    await assert.rejects(connectView({ contract, route: "page", mount: false, hydrate }), (error: unknown) =>
      error instanceof BridgeError && error.kind === "failed" && error.message.includes("uses Views protocol 4"));
    assert.deepEqual(diagnostics.map(diagnostic => [diagnostic.code, diagnostic.route]), [["protocol", "pageSnapshot"]]);
  });
});
