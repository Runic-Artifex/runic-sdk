// The development server's OTLP export of the page's Bridge call spans: where it exports (the OpenTelemetry variables or
// the `otlp` option), the OTLP/JSON it builds (the trace id and span id of the traceparent the call's request carried to
// .NET, so the .NET span is its child), batching and failures, and the path from a page's `runic:span` message to an
// OTLP/HTTP receiver through a Vite server.
import assert from "node:assert/strict";
import { createServer as createHttpServer } from "node:http";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { createServer } from "vite";
import { runic } from "../dist/index.js";
import { createOtlpForwarder, otlpTraces, readBrowserSpan, resolveOtlp } from "../dist/otlp.js";

const traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
const spanId = "00f067aa0ba902b7";
const traceparent = `00-${traceId}-${spanId}-01`;
const message = {
  traceparent, name: "command EditorViewModel.save", start: 1_700_000_000_010.25, end: 1_700_000_000_022.5,
  kind: "command", request: "command", route: "editor", contract: "Demo.EditorViewModel:ABC", member: "save",
  outcome: "ok", revision: 4, handler: { name: "EditorViewModel.Save", duration: 8.125 },
};

test("the export follows the OpenTelemetry variables and the otlp option", () => {
  const root = "/nonexistent";
  assert.equal(resolveOtlp(undefined, {}, root), undefined);
  assert.deepEqual(resolveOtlp(undefined, { OTEL_EXPORTER_OTLP_ENDPOINT: "http://localhost:4318/", OTEL_SERVICE_NAME: "Notes" }, root),
    { url: "http://localhost:4318/v1/traces", headers: {}, serviceName: "Notes-page" });
  assert.equal(resolveOtlp(undefined, {
    OTEL_EXPORTER_OTLP_ENDPOINT: "http://localhost:4318", OTEL_EXPORTER_OTLP_TRACES_ENDPOINT: "http://collector:9999/traces",
  }, root).url, "http://collector:9999/traces");
  assert.deepEqual(resolveOtlp(undefined, {
    OTEL_EXPORTER_OTLP_ENDPOINT: "http://localhost:4318", OTEL_EXPORTER_OTLP_HEADERS: "x-otlp-api-key=a%20b, other = c",
  }, root).headers, { "x-otlp-api-key": "a b", other: "c" });
  assert.equal(resolveOtlp(undefined, { OTEL_EXPORTER_OTLP_ENDPOINT: "http://localhost:4318" }, root).serviceName, "runic-page");
  // Turned off by the variables or the option.
  assert.equal(resolveOtlp(undefined, { OTEL_EXPORTER_OTLP_ENDPOINT: "http://localhost:4318", OTEL_SDK_DISABLED: "true" }, root), undefined);
  assert.equal(resolveOtlp(undefined, { OTEL_EXPORTER_OTLP_ENDPOINT: "http://localhost:4318", OTEL_TRACES_EXPORTER: "none" }, root), undefined);
  assert.equal(resolveOtlp(false, { OTEL_EXPORTER_OTLP_ENDPOINT: "http://localhost:4318" }, root), undefined);
  // The option wins.
  assert.deepEqual(resolveOtlp({ url: "http://127.0.0.1:1/v1/traces", headers: { a: "b" }, serviceName: "page" }, { OTEL_EXPORTER_OTLP_ENDPOINT: "http://x" }, root),
    { url: "http://127.0.0.1:1/v1/traces", headers: { a: "b" }, serviceName: "page" });
  assert.throws(() => resolveOtlp(true, {}, root), /needs otlp.url/u);
  assert.throws(() => resolveOtlp({ url: "file:///tmp/x" }, {}, root), /http or https/u);
  assert.throws(() => runic({ otlp: "yes" }), /otlp must be/u);
});

test("the service name defaults to the Vite root's package name", async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-otlp-"));
  try {
    await writeFile(join(root, "package.json"), JSON.stringify({ name: "@demo/notes-frontend" }));
    assert.equal(resolveOtlp(undefined, { OTEL_EXPORTER_OTLP_ENDPOINT: "http://localhost:4318" }, root).serviceName, "notes-frontend-page");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("a span becomes OTLP/JSON with the traceparent's ids, a client span kind and the Bridge attributes", () => {
  const span = readBrowserSpan(message);
  const request = otlpTraces([span, readBrowserSpan({ ...message, outcome: "error", reason: "domain-failed", handler: undefined })], "notes-page", "0.20.0");
  const [resource] = request.resourceSpans;
  assert.deepEqual(resource.resource.attributes[0], { key: "service.name", value: { stringValue: "notes-page" } });
  const [ok, failed] = resource.scopeSpans[0].spans;
  assert.equal(ok.traceId, traceId);
  assert.equal(ok.spanId, spanId);
  assert.equal(ok.parentSpanId, undefined);
  assert.equal(ok.kind, 3);
  assert.equal(ok.name, "command EditorViewModel.save");
  // fixed64 nanoseconds as decimal strings, exact beyond 2^53.
  assert.equal(ok.startTimeUnixNano, "1700000000010250000");
  assert.equal(ok.endTimeUnixNano, "1700000000022500000");
  const attributes = Object.fromEntries(ok.attributes.map(({ key, value }) => [key, Object.values(value)[0]]));
  assert.deepEqual(attributes, {
    "runic.bridge.kind": "command", "runic.bridge.request": "command", "runic.bridge.route": "editor",
    "runic.bridge.model": "EditorViewModel", "runic.bridge.member": "save", "runic.bridge.contract": "Demo.EditorViewModel:ABC",
    "runic.bridge.outcome": "ok", "runic.bridge.revision": "4", "runic.bridge.handler": "EditorViewModel.Save",
    "runic.bridge.handler.duration_ms": 8.125,
  });
  assert.equal(ok.status, undefined);
  assert.deepEqual(failed.status, { code: 2, message: "domain-failed" });
  assert.ok(failed.attributes.some(({ key, value }) => key === "error.type" && value.stringValue === "domain-failed"));
  // The JSON round-trips.
  assert.deepEqual(JSON.parse(JSON.stringify(request)), request);
});

test("a message that is not a well-formed span is dropped", () => {
  for (const bad of [
    null, "x", { ...message, traceparent: "nope" }, { ...message, traceparent: `00-${"0".repeat(32)}-${spanId}-01` },
    { ...message, traceparent: `00-${traceId}-${"0".repeat(16)}-01` }, { ...message, start: "1" }, { ...message, end: message.start - 1 },
    { ...message, name: "" }, { ...message, start: Number.NaN },
  ]) assert.equal(readBrowserSpan(bad), undefined, JSON.stringify(bad));
  const long = readBrowserSpan({ ...message, route: "r".repeat(10_000), handler: { name: "h", duration: "slow" } });
  assert.equal(long.route.length, 256);
  assert.equal(long.handler, undefined);
});

test("the forwarder batches spans, sends the configured headers and warns once while the endpoint fails", async () => {
  const posts = [];
  const warnings = [];
  let fail = true;
  const forwarder = createOtlpForwarder({ url: "http://127.0.0.1:1/v1/traces", headers: { "x-otlp-api-key": "k" }, serviceName: "page" }, {
    delay: 5, version: "0.20.0", warn: (warning) => warnings.push(warning),
    fetch: async (url, init) => { posts.push({ url, init }); if (fail) throw new Error("connection refused"); return { ok: true, status: 200 }; },
  });
  forwarder.add(message);
  forwarder.add({ bad: true });
  forwarder.add({ ...message, traceparent: `00-${traceId}-${"1".repeat(16)}-01` });
  await new Promise((resolve) => setTimeout(resolve, 30));
  await forwarder.flush();
  assert.equal(posts.length, 1);
  assert.equal(posts[0].init.headers["x-otlp-api-key"], "k");
  assert.equal(posts[0].init.headers["Content-Type"], "application/json");
  assert.equal(JSON.parse(posts[0].init.body).resourceSpans[0].scopeSpans[0].spans.length, 2);
  forwarder.add(message);
  await forwarder.flush();
  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /OTLP\/HTTP/u);
  // Recovers, and a later failure is reported again.
  fail = false;
  forwarder.add(message);
  await forwarder.flush();
  fail = true;
  forwarder.add(message);
  await forwarder.close();
  assert.equal(warnings.length, 2);
  forwarder.add(message);
  await forwarder.flush();
  assert.equal(posts.length, 4, "a closed forwarder posts nothing");
});

test("a Vite server with runic() forwards a page's span to an OTLP/HTTP receiver", async () => {
  const received = [];
  const receiver = createHttpServer((request, response) => {
    const chunks = [];
    request.on("data", (chunk) => chunks.push(chunk));
    request.on("end", () => {
      received.push({ url: request.url, type: request.headers["content-type"], body: JSON.parse(Buffer.concat(chunks).toString("utf8")) });
      response.writeHead(200, { "Content-Type": "application/json" }).end("{}");
    });
  });
  await new Promise((resolve) => receiver.listen(0, "127.0.0.1", resolve));
  const endpoint = `http://127.0.0.1:${receiver.address().port}`;
  const server = await createServer({
    configFile: false, logLevel: "silent", root: process.cwd(),
    plugins: [runic({ otlp: { url: `${endpoint}/v1/traces`, serviceName: "notes-page" } })],
    server: { host: "127.0.0.1", port: 0, strictPort: false },
  });
  try {
    await server.listen();
    const base = `http://127.0.0.1:${server.httpServer.address().port}`;
    // The served client options turn tracing on.
    const options = await (await fetch(`${base}/@id/__x00__virtual:runic/client-options`)).text();
    assert.match(options, /"trace":true/u);
    const response = await fetch(`${base}/__runic/diagnostics`, {
      method: "POST", headers: { "Content-Type": "application/json", Origin: base },
      body: JSON.stringify({ event: "runic:span", data: message }),
    });
    assert.equal(response.status, 204);
    for (let waited = 0; received.length === 0 && waited < 200; waited++) await new Promise((resolve) => setTimeout(resolve, 10));
    assert.equal(received.length, 1);
    assert.equal(received[0].url, "/v1/traces");
    assert.equal(received[0].type, "application/json");
    const [span] = received[0].body.resourceSpans[0].scopeSpans[0].spans;
    assert.equal(span.traceId, traceId);
    assert.equal(span.spanId, spanId);
  } finally {
    await server.close();
    await new Promise((resolve) => receiver.close(resolve));
  }
});

test("without an endpoint the served client options leave tracing off", async () => {
  const saved = { endpoint: process.env.OTEL_EXPORTER_OTLP_ENDPOINT, traces: process.env.OTEL_EXPORTER_OTLP_TRACES_ENDPOINT };
  delete process.env.OTEL_EXPORTER_OTLP_ENDPOINT;
  delete process.env.OTEL_EXPORTER_OTLP_TRACES_ENDPOINT;
  const server = await createServer({ configFile: false, logLevel: "silent", root: process.cwd(), plugins: [runic()],
    server: { host: "127.0.0.1", port: 0, strictPort: false } });
  try {
    await server.listen();
    const base = `http://127.0.0.1:${server.httpServer.address().port}`;
    const options = await (await fetch(`${base}/@id/__x00__virtual:runic/client-options`)).text();
    assert.doesNotMatch(options, /"trace"/u);
  } finally {
    await server.close();
    if (saved.endpoint !== undefined) process.env.OTEL_EXPORTER_OTLP_ENDPOINT = saved.endpoint;
    if (saved.traces !== undefined) process.env.OTEL_EXPORTER_OTLP_TRACES_ENDPOINT = saved.traces;
  }
});
