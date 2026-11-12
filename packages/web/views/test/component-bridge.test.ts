import assert from "node:assert/strict";
import { test } from "node:test";
import { connectComponentBridge } from "../src/component-bridge.js";
import { startComponentGuest } from "./component-guest.js";

// connectComponentBridge (region-component/1): the component's Bridge to its guest, over the guest's page wire.

async function until(condition: () => boolean, message: string, limit = 5_000): Promise<void> {
  const deadline = Date.now() + limit;
  while (!condition()) {
    if (Date.now() > deadline) assert.fail(message);
    await new Promise(resolve => setTimeout(resolve, 10));
  }
}

test("connectComponentBridge offers the token as its subprotocol, sends requests and receives pushes", async () => {
  const guest = startComponentGuest({ answer: request => JSON.stringify({ ok: true, echo: JSON.parse(request) }) });
  const bridge = connectComponentBridge({ url: guest.url, token: guest.token });
  try {
    const states: boolean[] = [];
    const stop = bridge.onConnectionChange(connected => states.push(connected));
    await until(() => bridge.isConnected(), "The bridge did not connect.");
    assert.deepEqual(states, [false, true]);
    assert.equal(guest.upgrades.length, 1);
    assert.equal(guest.upgrades[0]!.protocols, `runic-desktop.${guest.token}`);
    // The token check carries no window token: the component token admitted the socket.
    await until(() => guest.checks.length === 1, "No token check arrived.");
    assert.deepEqual(guest.checks, [0]);

    const reply = await bridge.request(JSON.stringify({ kind: "hello", text: "grüße" }));
    assert.deepEqual(JSON.parse(reply), { ok: true, echo: { kind: "hello", text: "grüße" } });
    // A request larger than one packet goes in chunks.
    const large = "x".repeat(200_000);
    assert.equal(JSON.parse(await bridge.request(JSON.stringify({ kind: "large", large }))).echo.large, large);

    const pushes: string[] = [];
    bridge.onMessage(message => pushes.push(message));
    guest.push('{"kind":"state","value":1}');
    await until(() => pushes.length === 1, "The push did not arrive.");
    assert.equal(pushes[0], '{"kind":"state","value":1}');

    // The guest's script never runs in the host page's realm.
    guest.runScript("globalThis.ran = true");
    await until(() => guest.scriptReplies.length === 1, "The script request got no reply.");
    assert.equal(guest.scriptReplies[0]![0], true);
    assert.equal((globalThis as { ran?: boolean }).ran, undefined);
    stop();
  } finally {
    bridge.close();
    guest.stop();
  }
});

test("connectComponentBridge connects again after a loss and stops once its token is refused or it is closed", async () => {
  const guest = startComponentGuest();
  const bridge = connectComponentBridge({ url: guest.url, token: guest.token });
  try {
    await until(() => bridge.isConnected(), "The bridge did not connect.");
    // A detach revokes the token: the socket closes, and every later upgrade is refused.
    guest.revoke();
    await until(() => !bridge.isConnected(), "The bridge did not see the close.");
    await assert.rejects(bridge.request("{}"), /not open/);
    await until(() => guest.upgrades.length >= 2, "The bridge did not try again.");
    assert.equal(guest.upgrades.at(-1)!.admitted, false);
    bridge.close();
    const tries = guest.upgrades.length;
    await new Promise(resolve => setTimeout(resolve, 1_200));
    assert.equal(guest.upgrades.length, tries, "A closed bridge kept connecting.");
  } finally {
    bridge.close();
    guest.stop();
  }
});

test("connectComponentBridge refuses an endpoint that is not a WebSocket address with a token", () => {
  assert.throws(() => connectComponentBridge({ url: "http://127.0.0.1:1/_webui_ws_connect", token: "t" }), TypeError);
  assert.throws(() => connectComponentBridge({ url: "ws://127.0.0.1:1/_webui_ws_connect", token: "" }), TypeError);
  assert.throws(() => connectComponentBridge({ url: "ws://127.0.0.1:1/_webui_ws_connect", token: "a b" }), TypeError);
});
