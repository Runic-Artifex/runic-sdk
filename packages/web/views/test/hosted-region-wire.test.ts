import assert from "node:assert/strict";
import { test } from "node:test";
import { readPush } from "../dist/protocol.js";

// hosted-regions/1: the runtime reads the feature's pushes as window pushes, and drops malformed ones.
const content = { kind: "hosted-region-content", name: "check", embedding: "frame", src: "http://[::1]:40117/_runic_frame/x", title: null, allow: "" };
// region-component/1: the guest's module, its surface's WebSocket address and the component token.
const component = {
  kind: "hosted-region-content", name: "check", embedding: "component",
  module: "http://127.0.0.1:40117/w1/_runic_component/component.js", url: "ws://127.0.0.1:40117/w1/_webui_ws_connect", token: "t".repeat(43),
};

test("hosted region pushes are window pushes without a route", () => {
  for (const push of [content, { kind: "hosted-region-update", name: "check", title: "Check", height: 320 }, { kind: "hosted-region-update", name: "check" },
    { kind: "hosted-region-empty", name: "check", reason: "ended" }, { kind: "hosted-region-focus", name: "check", route: "ignored" },
    component, { kind: "hosted-region-content", name: "check", embedding: "native", handle: 1 }]) {
    const reading = readPush(JSON.stringify(push));
    assert.equal(reading.result, "valid", JSON.stringify(push));
    assert.equal(reading.result === "valid" ? reading.push.route : "x", undefined);
  }
});

test("malformed hosted region pushes are invalid", () => {
  for (const push of [{ ...content, name: "a b" }, { ...content, name: undefined }, { ...content, src: "javascript:alert(1)" },
    { ...content, src: "" }, { ...content, allow: 1 }, { ...content, title: "t".repeat(1025) }, { ...content, embedding: undefined },
    { kind: "hosted-region-update", name: "check", height: -1 }, { kind: "hosted-region-update", name: "check", height: 1.5 },
    { kind: "hosted-region-update", name: "check", title: 3 }, { kind: "hosted-region-empty", name: "check" },
    { ...component, module: "m" }, { ...component, module: "javascript:alert(1)" }, { ...component, url: "http://127.0.0.1:40117/" },
    { ...component, token: undefined }, { ...component, token: "a b" }, { ...component, token: "t".repeat(257) }])
    assert.equal(readPush(JSON.stringify(push)).result, "invalid", JSON.stringify(push));
});

test("colorScheme in content and update pushes keeps them valid, also with a value the client does not know", () => {
  for (const colorScheme of ["light", "dark", null, "sepia", 3])
    for (const push of [{ ...content, colorScheme }, { ...component, colorScheme }, { kind: "hosted-region-update", name: "check", colorScheme }])
      assert.equal(readPush(JSON.stringify(push)).result, "valid", JSON.stringify(push));
});
