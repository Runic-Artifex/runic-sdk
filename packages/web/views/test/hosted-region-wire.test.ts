import assert from "node:assert/strict";
import { test } from "node:test";
import { readPush } from "../dist/protocol.js";

// hosted-regions/1: the runtime reads the feature's pushes as window pushes, and drops malformed ones.
const content = { kind: "hosted-region-content", name: "check", embedding: "frame", src: "http://[::1]:40117/_runic_frame/x", title: null, allow: "" };

test("hosted region pushes are window pushes without a route", () => {
  for (const push of [content, { kind: "hosted-region-update", name: "check", title: "Check", height: 320 }, { kind: "hosted-region-update", name: "check" },
    { kind: "hosted-region-empty", name: "check", reason: "ended" }, { kind: "hosted-region-focus", name: "check", route: "ignored" },
    { kind: "hosted-region-content", name: "check", embedding: "component", module: "m" }]) {
    const reading = readPush(JSON.stringify(push));
    assert.equal(reading.result, "valid", JSON.stringify(push));
    assert.equal(reading.result === "valid" ? reading.push.route : "x", undefined);
  }
});

test("malformed hosted region pushes are invalid", () => {
  for (const push of [{ ...content, name: "a b" }, { ...content, name: undefined }, { ...content, src: "javascript:alert(1)" },
    { ...content, src: "" }, { ...content, allow: 1 }, { ...content, title: "t".repeat(1025) }, { ...content, embedding: undefined },
    { kind: "hosted-region-update", name: "check", height: -1 }, { kind: "hosted-region-update", name: "check", height: 1.5 },
    { kind: "hosted-region-update", name: "check", title: 3 }, { kind: "hosted-region-empty", name: "check" }])
    assert.equal(readPush(JSON.stringify(push)).result, "invalid", JSON.stringify(push));
});
