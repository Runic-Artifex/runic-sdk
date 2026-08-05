import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

import {
  CAPABILITIES,
  FAULT_CODES,
  PROTOCOL_IDENTITY,
  PROTOCOL_LIMITS,
  PROTOCOL_VERSION,
  ProtocolValidationError,
  assertClientMessage,
  assertHostMessage,
  parseClientMessage,
  parseHostMessage,
  stringifyClientMessage,
  stringifyHostMessage,
  validateClientMessage,
  validateHostMessage,
} from "../dist/esm/index.js";

const corpusRoot = new URL("../../../../protocol/mvvm/corpus/v1/", import.meta.url);
const manifest = JSON.parse(await readFile(new URL("manifest.json", corpusRoot), "utf8"));

const ids = Object.freeze({
  session: "00000000-0000-4000-8000-000000000004",
  view: "abcdef00-0000-4000-8000-000000000002",
  request: "00000000-0000-4000-8000-000000000005",
  capability: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
});

function setProperty(overrides = {}) {
  return {
    v: 1,
    kind: "setProperty",
    session: ids.session,
    view: ids.view,
    request: ids.request,
    baseRevision: 0n,
    capability: ids.capability,
    payload: { member: 1, value: "ready" },
    ...overrides,
  };
}

function executeJson(argument) {
  return JSON.stringify({
    v: 1,
    kind: "execute",
    session: ids.session,
    view: ids.view,
    request: ids.request,
    baseRevision: 0,
    capability: ids.capability,
    payload: { member: 1, argument },
  });
}

function expectProtocolError(action) {
  assert.throws(action, (error) => {
    assert.ok(error instanceof ProtocolValidationError);
    assert.equal(typeof error.code, "string");
    assert.equal(typeof error.path, "string");
    return true;
  });
}

test("exports the frozen runic.toolkit.mvvm/1 protocol contract", () => {
  assert.equal(PROTOCOL_IDENTITY, "runic.toolkit.mvvm/1");
  assert.equal(PROTOCOL_VERSION, 1);
  assert.deepEqual([...CAPABILITIES], [
    "cancellation",
    "collections",
    "commandResults",
    "patches",
    "validation",
  ]);
  assert.deepEqual([...FAULT_CODES], [
    "protocol.unsupported",
    "request.invalid",
    "member.unknown",
    "revision.stale",
    "limit.exceeded",
    "request.cancelled",
    "request.timeout",
    "session.closed",
  ]);
  assert.equal(PROTOCOL_LIMITS.maxFrameBytes, 1_048_576);
  assert.equal(PROTOCOL_LIMITS.maxJsonDepth, 32);
  assert.equal(PROTOCOL_LIMITS.maxSnapshotMembers, 4_096);
  assert.equal(PROTOCOL_LIMITS.maxPatchChanges, 1_024);
});

test("accepts every valid canonical corpus document", async (t) => {
  for (const entry of manifest.cases.filter((item) => item.valid)) {
    await t.test(entry.id, async () => {
      const source = await readFile(new URL(entry.file, corpusRoot), "utf8");
      const parse = entry.schema === "client" ? parseClientMessage : parseHostMessage;
      if (entry.documentMode === "eachItem") {
        for (const item of JSON.parse(source)) {
          assert.equal(parse(JSON.stringify(item)).v, 1);
        }
      } else {
        assert.equal(parse(source).v, 1);
      }
    });
  }
});

test("rejects every invalid canonical corpus document", async (t) => {
  for (const entry of manifest.cases.filter((item) => !item.valid)) {
    await t.test(entry.id, async () => {
      const source = await readFile(new URL(entry.file, corpusRoot), "utf8");
      const parse = entry.schema === "client" ? parseClientMessage : parseHostMessage;
      expectProtocolError(() => parse(source));
    });
  }
});

test("preserves signed 64-bit revisions without binary64 rounding", () => {
  const message = parseClientMessage(`{
    "v":1,"kind":"setProperty",
    "session":"${ids.session}","view":"${ids.view}","request":"${ids.request}",
    "baseRevision":9223372036854775807,
    "capability":"${ids.capability}",
    "payload":{"member":2147483647,"value":null}
  }`);

  assert.equal(message.baseRevision, 9_223_372_036_854_775_807n);
  assert.equal(typeof message.baseRevision, "bigint");
  assert.match(stringifyClientMessage(message), /"baseRevision":9223372036854775807/);
});

test("normalizes raw and decoded negative-zero revisions to canonical zero", () => {
  const raw = stringifyClientMessage(setProperty()).replace('"baseRevision":0', '"baseRevision":-0');
  const parsed = parseClientMessage(raw);
  const decoded = validateClientMessage(setProperty({ baseRevision: -0 }));

  assert.equal(parsed.baseRevision, 0n);
  assert.equal(decoded.baseRevision, 0n);
  assert.match(stringifyClientMessage(parsed), /"baseRevision":0/);
  assert.match(stringifyClientMessage(decoded), /"baseRevision":0/);
});

test("validates already-decoded client and host values", () => {
  const client = validateClientMessage(setProperty());
  assert.equal(client.baseRevision, 0n);
  assert.doesNotThrow(() => assertClientMessage(client));

  const host = validateHostMessage({
    v: 1,
    kind: "snapshot",
    session: ids.session,
    view: ids.view,
    request: ids.request,
    payload: {
      revision: 14n,
      members: [{ type: "validation", member: 1, errors: ["Required"] }],
    },
  });
  assert.equal(host.payload.revision, 14n);
  assert.doesNotThrow(() => assertHostMessage(host));
});

test("enforces closed envelopes and payloads before dispatch", () => {
  expectProtocolError(() =>
    validateClientMessage({ ...setProperty(), method: "DeleteEverything" }),
  );
  expectProtocolError(() =>
    validateClientMessage({
      ...setProperty(),
      payload: { member: 1, value: null, clrType: "System.IO.File" },
    }),
  );
  expectProtocolError(() =>
    validateClientMessage({ ...setProperty(), view: ids.view.toUpperCase() }),
  );
});

test("rejects malformed JSON framing and duplicate object keys", () => {
  const valid = JSON.stringify({
    v: 1,
    kind: "handshake",
    request: ids.request,
    payload: { supportedVersions: [1], capabilities: [] },
  });
  expectProtocolError(() => parseClientMessage(`\uFEFF${valid}`));
  expectProtocolError(() => parseClientMessage(`${valid} null`));
  expectProtocolError(() => parseClientMessage("{\"v\":1,\"v\":1}"));
  expectProtocolError(() => parseClientMessage("{/*comment*/\"v\":1}"));
  expectProtocolError(() => parseClientMessage("{\"v\":1,}"));
});

test("rejects invalid UTF-8 before JSON interpretation", () => {
  const invalidUtf8 = Uint8Array.from([0x7b, 0x22, 0x78, 0x22, 0x3a, 0xc3, 0x28, 0x7d]);
  expectProtocolError(() => parseClientMessage(invalidUtf8));
});

test("enforces encoded frame byte limits", () => {
  const source = JSON.stringify({
    v: 1,
    kind: "handshake",
    request: ids.request,
    payload: { supportedVersions: [1], capabilities: [] },
  });
  assert.equal(parseClientMessage(source, { maxFrameBytes: Buffer.byteLength(source) }).kind, "handshake");
  expectProtocolError(() =>
    parseClientMessage(source, { maxFrameBytes: Buffer.byteLength(source) - 1 }),
  );
});

test("counts contract limits in UTF-8 bytes rather than UTF-16 units", () => {
  const open = (contract) =>
    JSON.stringify({
      v: 1,
      kind: "open",
      contract,
      view: ids.view,
      request: ids.request,
      payload: {},
    });

  assert.equal(parseClientMessage(open("é".repeat(64))).contract, "é".repeat(64));
  expectProtocolError(() => parseClientMessage(open("é".repeat(65))));
});

test("bounds nesting depth before consumer-visible values are created", () => {
  let atLimit = null;
  // Envelope object + payload object + 30 nested arrays = the exact depth-32 ceiling.
  for (let index = 0; index < 30; index += 1) atLimit = [atLimit];
  assert.equal(parseClientMessage(executeJson(atLimit)).kind, "execute");
  assert.equal(validateClientMessage(JSON.parse(executeJson(atLimit))).kind, "execute");

  let tooDeep = null;
  for (let index = 0; index < 31; index += 1) tooDeep = [tooDeep];
  expectProtocolError(() => parseClientMessage(executeJson(tooDeep)));
  expectProtocolError(() => validateClientMessage(JSON.parse(executeJson(tooDeep))));

  let hostAtLimit = null;
  for (let index = 0; index < 28; index += 1) hostAtLimit = [hostAtLimit];
  assert.equal(validateHostMessage({
    v: 1,
    kind: "snapshot",
    session: ids.session,
    view: ids.view,
    request: ids.request,
    payload: { revision: 14n, members: [{ type: "property", member: 1, value: hostAtLimit }] },
  }).kind, "snapshot");

  let hostTooDeep = null;
  for (let index = 0; index < 29; index += 1) hostTooDeep = [hostTooDeep];
  expectProtocolError(() => validateHostMessage({
    v: 1,
    kind: "snapshot",
    session: ids.session,
    view: ids.view,
    request: ids.request,
    payload: { revision: 14n, members: [{ type: "property", member: 1, value: hostTooDeep }] },
  }));
});

test("keeps canonical snapshot member order compatible with the shared corpus", async () => {
  const source = await readFile(new URL("valid/host-opened.json", corpusRoot), "utf8");
  const opened = parseHostMessage(source);

  // Property validation immediately follows its property while the remaining
  // members stay in ascending member-id order. Preserve that canonical wire order.
  assert.deepEqual(opened.payload.snapshot.members.map((member) => member.member), [1, 1, 2, 3]);
});

test("bounds general strings, arrays, object properties, and property-name bytes", () => {
  assert.equal(parseClientMessage(executeJson("x".repeat(65_536))).kind, "execute");
  expectProtocolError(() => parseClientMessage(executeJson("x".repeat(65_537))));
  assert.equal(parseClientMessage(executeJson(Array.from({ length: 10_000 }, () => null))).kind, "execute");
  expectProtocolError(() => parseClientMessage(executeJson(Array.from({ length: 10_001 }, () => null))));

  const maximumProperties = {};
  for (let index = 0; index < 4_096; index += 1) maximumProperties[`p${index}`] = null;
  assert.equal(parseClientMessage(executeJson(maximumProperties)).kind, "execute");
  const manyProperties = {};
  for (let index = 0; index < 4_097; index += 1) manyProperties[`p${index}`] = null;
  expectProtocolError(() => parseClientMessage(executeJson(manyProperties)));
  assert.equal(parseClientMessage(executeJson({ ["x".repeat(128)]: null })).kind, "execute");
  expectProtocolError(() => parseClientMessage(executeJson({ ["é".repeat(65)]: null })));
});

test("rejects host patches that are not one consecutive transition", () => {
  expectProtocolError(() =>
    validateHostMessage({
      v: 1,
      kind: "patch",
      session: ids.session,
      view: ids.view,
      payload: {
        fromRevision: 12n,
        toRevision: 14n,
        changes: [{ type: "property", member: 1, value: "skipped" }],
      },
    }),
  );
});

test("requires stale-revision recovery fields and accepts the canonical fault", () => {
  const fault = {
    v: 1,
    kind: "fault",
    session: ids.session,
    view: ids.view,
    request: ids.request,
    payload: {
      code: "revision.stale",
      message: "The request is based on an obsolete revision.",
      retryable: true,
      currentRevision: 14n,
      snapshotRequired: true,
    },
  };
  assert.equal(validateHostMessage(fault).payload.currentRevision, 14n);
  assert.match(stringifyHostMessage(fault), /"currentRevision":14/);

  const { currentRevision: _revision, ...incompletePayload } = fault.payload;
  expectProtocolError(() => validateHostMessage({ ...fault, payload: incompletePayload }));

  const { snapshotRequired: _snapshotRequired, ...missingSnapshotRequired } = fault.payload;
  expectProtocolError(() => validateHostMessage({ ...fault, payload: missingSnapshotRequired }));
  expectProtocolError(() =>
    validateHostMessage({ ...fault, payload: { ...fault.payload, snapshotRequired: false } }),
  );
});

test("accepts schema-valid recovery hints on non-stale faults", () => {
  const fault = validateHostMessage({
    v: 1,
    kind: "fault",
    session: ids.session,
    view: ids.view,
    request: ids.request,
    payload: {
      code: "request.timeout",
      message: "The request timed out.",
      retryable: true,
      currentRevision: 14n,
      snapshotRequired: false,
    },
  });

  assert.equal(fault.payload.currentRevision, 14n);
  assert.equal(fault.payload.snapshotRequired, false);
});
