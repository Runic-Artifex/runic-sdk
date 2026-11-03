// client-errors/1 on the client (specs/application/README.md, "Page errors"): installErrorReporting sends uncaught
// errors, unhandled rejections and runtime-caught errors as bounded, redacted, rate-limited `client-error` requests,
// only on a connection that uses the feature.
import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import { installErrorReporting, type RunicBridgeClient } from "../dist/index.js";
import { sharedRuntimeFor } from "../dist/runtime.js";
import { reportBridgeError } from "../dist/transport.js";
import { createMockBridge, installMockBridge } from "../dist/mock.js";

const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
const host = globalThis as unknown as Record<string | symbol, unknown>;
const tick = (ms = 0) => new Promise(resolve => setTimeout(resolve, ms));

// The page's window events: Node has no window, so the test gives globalThis an EventTarget's listeners.
const events = new EventTarget();
host["addEventListener"] = events.addEventListener.bind(events);
host["removeEventListener"] = events.removeEventListener.bind(events);
host["location"] = { origin: "https://app.example", pathname: "/editor/index.html", href: "https://app.example/editor/index.html?token=secret#x" };

// The runtime hands errors it caught to reportError, which Bun raises as an uncaught error.
const reported: unknown[] = [];
host["reportError"] = (error: unknown) => { reported.push(error); };

let uninstall: (() => void) | undefined;
afterEach(() => { uninstall?.(); uninstall = undefined; });

function errorEvent(error: unknown, message = "Uncaught"): Event {
  const event = new Event("error") as Event & { error: unknown; message: string };
  event.error = error;
  event.message = message;
  return event;
}

function rejection(reason: unknown): Event {
  const event = new Event("unhandledrejection") as Event & { reason: unknown };
  event.reason = reason;
  return event;
}

/** A host bridge whose producer lists `features` and records every client-error. */
function installBridge(features: readonly string[]) {
  const reports: Record<string, unknown>[] = [];
  const bridge: RunicBridgeClient = {
    isConnected: () => true,
    onConnectionChange(report) { report(true); return () => undefined; },
    onMessage() { return () => undefined; },
    async request(text) {
      const message = JSON.parse(text) as Record<string, unknown>;
      if (message["kind"] === "hello") return JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features });
      if (message["kind"] === "client-error") { reports.push(message); return JSON.stringify({ ok: true }); }
      return JSON.stringify({ ok: false, error: { kind: "rejected", reason: "invalid-request", message: "unexpected" } });
    },
  };
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  host["__runicBridge"] = bridge;
  sharedRuntimeFor(bridge);
  return reports;
}

async function settle(reports: readonly unknown[], count: number) {
  for (let waited = 0; reports.length < count && waited < 200; waited++) await tick(1);
  await tick(5);
}

test("uncaught errors, rejections and runtime errors are reported with the page URL without its query", async () => {
  const reports = installBridge(["client-errors/1"]);
  uninstall = installErrorReporting();
  const thrown = new TypeError("x is not a function at https://api.example/v1/items?key=abc");
  thrown.stack = "TypeError: x is not a function\n    at load (https://app.example/main.js?v=3:10:5)";

  events.dispatchEvent(errorEvent(thrown));
  events.dispatchEvent(rejection("plain reason"));
  reportBridgeError(new Error("subscriber failed"), "editor");
  await settle(reports, 3);

  assert.equal(reports.length, 3);
  assert.deepEqual(reports[0], {
    kind: "client-error", source: "error", message: "x is not a function at https://api.example/v1/items", name: "TypeError",
    stack: "TypeError: x is not a function\n    at load (https://app.example/main.js:10:5)", url: "https://app.example/editor/index.html",
  });
  assert.deepEqual([reports[1]!["source"], reports[1]!["message"], reports[1]!["name"]], ["unhandled-rejection", "plain reason", undefined]);
  assert.deepEqual([reports[2]!["source"], reports[2]!["route"]], ["runtime", "editor"]);
});

test("a runtime error the browser raises again as an error event is reported once", async () => {
  const reports = installBridge(["client-errors/1"]);
  uninstall = installErrorReporting();
  const error = new Error("once");
  reportBridgeError(error);
  events.dispatchEvent(errorEvent(error));
  await settle(reports, 1);
  assert.equal(reports.length, 1);
  assert.equal(reports[0]!["source"], "runtime");
});

test("reports are trimmed, rate-limited, deduplicated and redacted", async () => {
  const reports = installBridge(["client-errors/1"]);
  uninstall = installErrorReporting({
    maxReportsPerMinute: 3, maxMessageLength: 10, maxStackLines: 2,
    redact: report => report.message.startsWith("secret") ? undefined : report,
  });
  const long = new Error("0123456789abcdef");
  long.stack = "Error: 0123456789abcdef\n    at a\n    at b\n    at c";
  events.dispatchEvent(errorEvent(long));
  events.dispatchEvent(errorEvent(long));
  events.dispatchEvent(errorEvent(new Error("secret: 4111")));
  events.dispatchEvent(errorEvent(new Error("second")));
  events.dispatchEvent(errorEvent(new Error("third")));
  events.dispatchEvent(errorEvent(new Error("fourth")));
  await settle(reports, 3);

  assert.deepEqual(reports.map(report => report["message"]), ["012345678…", "second", "third"]);
  assert.equal(reports[0]!["stack"], "Error: 0123456789abcdef\n    at a");
  // The repeat of the first error and the fourth error, beyond the limit, are counted, and the count goes with a
  // report sent after them; the report redact dropped is not counted.
  assert.equal(reports.reduce((sum, report) => sum + ((report["dropped"] as number | undefined) ?? 0), 0), 2);
});

test("a cut never leaves half a surrogate pair", async () => {
  const reports = installBridge(["client-errors/1"]);
  uninstall = installErrorReporting({ maxMessageLength: 10 });
  // "😀" is two UTF-16 units; the ninth and tenth units of this message are one emoji, which the cut must not split.
  events.dispatchEvent(errorEvent(new Error("01234567😀tail")));
  events.dispatchEvent(errorEvent(new Error("0123456😀tail")));
  await settle(reports, 2);

  assert.deepEqual(reports.map(report => report["message"]), ["01234567…", "0123456😀…"]);
  for (const report of reports) assert.doesNotMatch(report["message"] as string, /[\uD800-\uDBFF](?![\uDC00-\uDFFF])/);
});

test("without client-errors/1 nothing is sent", async () => {
  const reports = installBridge(["presented/1"]);
  uninstall = installErrorReporting();
  events.dispatchEvent(errorEvent(new Error("lost")));
  await tick(20);
  assert.equal(reports.length, 0);
});

test("uninstalling stops reporting", async () => {
  const reports = installBridge(["client-errors/1"]);
  installErrorReporting()();
  events.dispatchEvent(errorEvent(new Error("after")));
  reportBridgeError(new Error("after"));
  await tick(20);
  assert.equal(reports.length, 0);
});

test("the mock bridge records client errors", async () => {
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  const mock = installMockBridge(createMockBridge());
  try {
    sharedRuntimeFor(mock);
    uninstall = installErrorReporting();
    events.dispatchEvent(errorEvent(new RangeError("out of range")));
    for (let round = 0; round < 50 && mock.clientErrors.length === 0; round++) { await mock.flush(); await tick(1); }
    assert.equal(mock.clientErrors.length, 1);
    assert.deepEqual([mock.clientErrors[0]!.source, mock.clientErrors[0]!.name, mock.clientErrors[0]!.message], ["error", "RangeError", "out of range"]);
  } finally { delete host["__runicBridge"]; }
});
