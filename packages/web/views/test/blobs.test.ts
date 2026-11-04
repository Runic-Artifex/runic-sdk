// blobs/1 on the page (specs/application/README.md, "Blobs and uploads"): the wire values, upload() through the mock
// Bridge and over XMLHttpRequest, and mockBlob.
import assert from "node:assert/strict";
import { test } from "node:test";
import { BridgeError, upload, type BlobRef, type RunicBridgeClient } from "../dist/index.js";
import { connectView } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge, mockBlob } from "../dist/mock.js";

const host = globalThis as unknown as { __runicBridge?: RunicBridgeClient; XMLHttpRequest?: unknown };

test("a blob decodes to {url, type, size, name} and an upload to its reference", () => {
  assert.deepEqual(bridgeWire.blob({ url: "http://h/_runic/blobs/t", type: "image/png", size: 3, name: "a.png", extra: 1 }),
    { url: "http://h/_runic/blobs/t", type: "image/png", size: 3, name: "a.png" });
  assert.deepEqual(bridgeWire.blob({ url: "", type: "text/csv", size: null, name: null }), { url: "", type: "text/csv", size: null, name: null });
  for (const invalid of [null, "url", { url: 1, type: "a/b", size: 1, name: null }, { url: "u", type: "a/b", size: -1, name: null },
    { url: "u", type: "a/b", size: 1.5, name: null }, { url: "u", type: "a/b", size: 1, name: 3 }])
    assert.throws(() => bridgeWire.blob(invalid), `${JSON.stringify(invalid)} decoded`);
  assert.deepEqual(bridgeWire.blobRef({ id: "abc", name: "n.txt", type: "text/plain", size: 5 }), { id: "abc", name: "n.txt", type: "text/plain", size: 5 });
  assert.throws(() => bridgeWire.blobRef({ id: "" }));
  assert.deepEqual(bridgeWire.encodeBlobRef({ id: "abc", name: "n", type: "t/x", size: 1 }), { id: "abc", name: "n", type: "t/x", size: 1 });
});

test("upload streams through the host bridge's upload and a command takes the reference", async () => {
  const bridge = installMockBridge(createMockBridge());
  let received: unknown;
  bridge.view("files", { state: { lastUpload: null }, commands: { attach: (_state, input) => { received = input; return { lastUpload: (input as BlobRef).name }; } } });
  const client = await connectView<{ lastUpload: string | null }>({
    contract: "Tests.Files:1", route: "files", members: { lastUpload: value => value === null ? null : bridgeWire.string(value) },
  });
  const progress: number[] = [];
  const ref = await upload(new File(["hello upload"], "notes.txt"), { type: "text/plain", onProgress: ({ loaded, total }) => progress.push(loaded / total) });
  assert.deepEqual(ref, { id: "mock-upload-00000001", name: "notes.txt", type: "text/plain", size: 12 });
  assert.deepEqual(progress, [0.5, 1]);
  assert.equal(await bridge.uploadContent(ref)?.text(), "hello upload");
  assert.equal(bridge.uploads.length, 1);
  await client.command("attach", bridgeWire.encodeBlobRef(ref));
  assert.deepEqual(received, { id: "mock-upload-00000001", name: "notes.txt", type: "text/plain", size: 12 });
  assert.equal(client.snapshot.lastUpload, "notes.txt");
  client.dispose();
});

test("upload rejects without a grant, over the limit, at capacity and when aborted", async () => {
  installMockBridge(createMockBridge({ uploads: false }));
  await assert.rejects(upload(new Blob(["x"])), (error: unknown) => error instanceof BridgeError && error.kind === "unavailable");

  const small = installMockBridge(createMockBridge({ uploads: { maximumBytes: 4 } }));
  await assert.rejects(upload(new Blob(["12345"])), (error: unknown) => error instanceof BridgeError && error.kind === "rejected" && error.reason === "too-large");
  assert.equal(small.uploads.length, 0);

  const full = installMockBridge(createMockBridge());
  full.failNextUpload({ status: 507, reason: "capacity", message: "No room." });
  await assert.rejects(upload(new Blob(["x"])), (error: unknown) => error instanceof BridgeError && error.kind === "rejected" && error.reason === "capacity");
  // A host that could not store the upload answers failed, as the spec's kinds say.
  full.failNextUpload({ status: 500, reason: "failed" });
  await assert.rejects(upload(new Blob(["x"])), (error: unknown) => error instanceof BridgeError && error.kind === "failed" && error.reason === "failed");

  // Aborted halfway: the mock stops before it stores the upload.
  const halfway = installMockBridge(createMockBridge());
  const controller = new AbortController();
  await assert.rejects(upload(new Blob(["xx"]), { signal: controller.signal, onProgress: () => controller.abort(new Error("stop")) }), /stop/);
  assert.equal(halfway.uploads.length, 0);
});

test("without a host upload, upload POSTs the file with the connection's token and reports progress", async () => {
  // A host bridge that grants uploads in its hello but sends none itself, as Runic Desktop and CS-WebUI.
  const bridge: RunicBridgeClient = {
    isConnected: () => true,
    request: async text => {
      const message = JSON.parse(text) as { kind: string };
      if (message.kind !== "hello") return JSON.stringify({ ok: true, revision: 0 });
      return JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test-host", version: "1" }, features: ["blobs/1"],
        blobs: { upload: "http://127.0.0.1:9/_runic/upload", token: "connection-token", maximumBytes: 1000 } });
    },
    onMessage: () => () => {},
    onConnectionChange: listener => { listener(true); return () => {}; },
  };
  host.__runicBridge = bridge;
  const sent: { method?: string; url?: string; headers: Record<string, string>; body?: unknown; credentials?: boolean } = { headers: {} };
  class FakeRequest {
    status = 0;
    responseText = "";
    withCredentials = false;
    readonly upload: { onprogress: ((event: { loaded: number; total: number; lengthComputable: boolean }) => void) | null } = { onprogress: null };
    onload: (() => void) | null = null;
    onerror: (() => void) | null = null;
    onabort: (() => void) | null = null;
    open(method: string, url: string) { sent.method = method; sent.url = url; }
    setRequestHeader(name: string, value: string) { sent.headers[name] = value; }
    abort() { this.onabort?.(); }
    send(body: unknown) {
      sent.body = body;
      sent.credentials = this.withCredentials;
      queueMicrotask(() => {
        this.upload.onprogress?.({ loaded: 2, total: 4, lengthComputable: true });
        this.status = 201;
        this.responseText = JSON.stringify({ ok: true, upload: { id: "u1", name: "ä b.txt", type: "text/plain", size: 4 } });
        this.onload?.();
      });
    }
  }
  const previous = host.XMLHttpRequest;
  host.XMLHttpRequest = FakeRequest;
  try {
    const progress: number[] = [];
    const file = new File(["data"], "ä b.txt", { type: "text/plain" });
    const ref = await upload(file, { onProgress: ({ loaded }) => progress.push(loaded) });
    assert.deepEqual(ref, { id: "u1", name: "ä b.txt", type: "text/plain", size: 4 });
    assert.equal(sent.method, "POST");
    assert.equal(sent.url, "http://127.0.0.1:9/_runic/upload");
    assert.deepEqual(sent.headers, { "X-Runic-Upload-Token": "connection-token", "X-Runic-Upload-Name": "%C3%A4%20b.txt", "Content-Type": file.type });
    assert.equal(sent.body, file);
    assert.equal(sent.credentials, true);
    assert.deepEqual(progress, [2, 4]);
  } finally {
    host.XMLHttpRequest = previous;
    delete host.__runicBridge;
  }
});

test("mockBlob makes a blob whose URL loads its content", async () => {
  const blob = mockBlob("avatar", { type: "image/png", name: "a.png" });
  assert.equal(blob.type, "image/png");
  assert.equal(blob.size, 6);
  assert.equal(blob.name, "a.png");
  assert.equal(await (await fetch(blob.url)).text(), "avatar");
  assert.equal(mockBlob(new Uint8Array([1, 2])).type, "application/octet-stream");
});
