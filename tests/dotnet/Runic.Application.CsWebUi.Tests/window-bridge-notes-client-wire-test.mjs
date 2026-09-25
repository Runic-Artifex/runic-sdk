import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { stripTypeScriptTypes } from "node:module";

const source = await readFile(new URL("./window-bridge-notes-client.ts", import.meta.url), "utf8");
const moduleUrl = `data:text/javascript;base64,${Buffer.from(stripTypeScriptTypes(source)).toString("base64")}`;
const { connectNotes, SaveUncertainError } = await import(moduleUrl);
const routeNames = ["__runicBridgeDocumentBegin", "notes.editor.mount", "notes.title.get", "notes.title.set", "notes.save.start", "notes.save.wait"];
const endpoints = Object.fromEntries(routeNames.map((name, index) =>
  [name, { endpoint: (index + 1).toString(16).toUpperCase().padStart(32, "0"), generation: index + 1 }]));
const routeByEndpoint = new Map(Object.entries(endpoints).map(([name, descriptor]) => [descriptor.endpoint, name]));

function installWire(replies) {
  const calls = [];
  globalThis.runicCsWebUi = { credential: "fixture", documentEpoch: "fixture-document", endpointRevision: 0, endpoints: { ...endpoints } };
  globalThis.__runicBridgeEndpointHandoff = ({ revision, endpoints: latest }) => {
    if (revision <= globalThis.runicCsWebUi.endpointRevision) return;
    globalThis.runicCsWebUi.endpoints = latest;
    globalThis.runicCsWebUi.endpointRevision = revision;
  };
  globalThis.webui = {
    async call(binding, credential, envelopeJson) {
      assert.equal(binding, "__runicBridgeDispatch");
      assert.equal(credential, "fixture");
      const envelope = JSON.parse(envelopeJson);
      const route = routeByEndpoint.get(envelope.endpoint);
      calls.push(route);
      assert.equal(envelope.payload.documentEpoch, "fixture-document");
      assert.ok(route, "A known fixture route was requested.");
      const reply = typeof replies[route] === "function" ? replies[route]() : replies[route];
      if (reply instanceof Error) throw reply;
      return JSON.stringify(reply);
    },
  };
  return calls;
}

installWire({
  __runicBridgeDocumentBegin: { ok: true, kind: "accepted", manifest: {
    revision: 1, endpoints: { ...endpoints,
      "notes.title.get": { ...endpoints["notes.title.get"], generation: "3" } },
  } },
});
await assert.rejects(connectNotes(), /Invalid __runicBridgeDocumentBegin reply: notes\.title\.get\.generation/);

installWire({
  __runicBridgeDocumentBegin: { ok: true, kind: "accepted", manifest: { revision: 1, endpoints } },
  "notes.editor.mount": { ok: true },
  "notes.title.get": { ok: true, snapshot: { value: "Draft", version: "0" }, error: null },
});
const editor = (await connectNotes()).editor();
await editor.mount();
await assert.rejects(editor.title(), /Invalid notes\.title\.get reply: version/);

installWire({
  __runicBridgeDocumentBegin: { ok: true, kind: "accepted", manifest: { revision: 1, endpoints } },
  "notes.editor.mount": { ok: true },
  "notes.title.set": { ok: false, kind: "surprise", current: { value: "Draft", version: 0 }, error: null },
});
const second = (await connectNotes()).editor();
await second.mount();
await assert.rejects(second.setTitle("No untyped result"), /Invalid notes\.title\.set reply: unknown title receipt/);

const lostAdmissionCalls = installWire({
  __runicBridgeDocumentBegin: { ok: true, kind: "accepted", manifest: { revision: 1, endpoints } },
  "notes.editor.mount": { ok: true },
  "notes.save.start": new Error("reply lost after possible admission"),
});
const third = (await connectNotes()).editor();
await third.mount();
await assert.rejects(third.save("known-admission-id"), error => {
  assert.ok(error instanceof SaveUncertainError);
  assert.equal(error.requestId, "known-admission-id");
  assert.equal(error.phase, "admission");
  return true;
});
assert.equal(lostAdmissionCalls.filter(route => route === "notes.save.start").length, 1);

const lostCompletionCalls = installWire({
  __runicBridgeDocumentBegin: { ok: true, kind: "accepted", manifest: { revision: 1, endpoints } },
  "notes.editor.mount": { ok: true },
  "notes.save.start": { accepted: true, requestId: "known-completion-id", kind: "running" },
  "notes.save.wait": { kind: "unexpected", state: null },
});
const fourth = (await connectNotes()).editor();
await fourth.mount();
await assert.rejects(fourth.save("known-completion-id"), error => {
  assert.ok(error instanceof SaveUncertainError);
  assert.equal(error.requestId, "known-completion-id");
  assert.equal(error.phase, "completion");
  return true;
});
assert.equal(lostCompletionCalls.filter(route => route === "notes.save.start").length, 1);
assert.equal(lostCompletionCalls.filter(route => route === "notes.save.wait").length, 1);

let beginCalls = 0;
const missedHandoffCalls = installWire({
  __runicBridgeDocumentBegin: () => ({ ok: true, kind: "accepted", manifest: { revision: ++beginCalls, endpoints } }),
  "notes.editor.mount": { ok: true },
  "notes.title.get": { ok: true, snapshot: { value: "Recovered", version: 1 }, error: null },
});
const fifth = (await connectNotes()).editor();
await fifth.mount();
delete globalThis.runicCsWebUi.endpoints["notes.title.get"];
assert.equal((await fifth.title()).value, "Recovered");
assert.equal(missedHandoffCalls.filter(route => route === "__runicBridgeDocumentBegin").length, 2);
assert.equal(missedHandoffCalls.filter(route => route === "notes.title.get").length, 1);

console.log("SDK_WINDOW_BRIDGE_NOTES_WIRE_DECODE_OK|invalid-manifest-rejected|malformed-title-rejected|unknown-receipt-rejected|lost-admission-id-retained|lost-completion-id-retained|no-start-replay|missing-descriptor-reconciled-before-call");
