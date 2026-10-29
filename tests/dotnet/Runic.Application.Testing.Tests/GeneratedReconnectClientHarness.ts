// Checks that a generated content client resumes after its host transport reconnects without a page reload: it sends
// hello with a new session, opens its presentation again with the same id, and shows the producer's new state.
import assert from "node:assert/strict";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";

const [generatedDirectory] = Bun.argv.slice(2);
if (!generatedDirectory) throw new Error("Usage: GeneratedReconnectClientHarness.ts <generated-dir>");
const load = (name: string) => import(pathToFileURL(resolve(generatedDirectory, `${name}.ts`)).href);
const bridge = installMockBridge(createMockBridge());
const { pageChild } = await load("child");
const { mockChild } = await load("child.mock");
const mock = mockChild(bridge, { id: "reconnect", state: { title: "before" } });

const view = await pageChild("reconnect").connect();
const titles: string[] = [];
view.subscribe((state: { readonly title: string }) => titles.push(state.title));
const requests = (kind: string) => bridge.calls.filter(call => call.kind === kind).map(call => call.message);
const [opened] = requests("open");
assert.equal(opened?.["route"], "contentreconnect", "The content client did not open its route.");

bridge.disconnect();
mock.update({ title: "after" });
bridge.reconnect();
const deadline = Date.now() + 5_000;
while (titles.at(-1) !== "after" && Date.now() < deadline) {
  await bridge.flush();
  await new Promise<void>(resolve => setTimeout(resolve, 10));
}
assert.equal(titles.at(-1), "after", "A reconnect did not refresh the route's state.");
const hellos = requests("hello");
assert.equal(hellos.length, 2, "A reconnect did not send hello again.");
assert.notEqual(hellos[1]?.["session"], hellos[0]?.["session"], "A reconnect reused the closed connection's session.");
const reopened = requests("open").slice(1);
assert.ok(reopened.length > 0 && reopened.every(open => open["presentation"] === opened["presentation"]),
  "A reconnect did not open the presentation again with its id.");

view.dispose();
await bridge.flush();
const closed = requests("close").at(-1);
assert.equal(closed?.["presentation"], opened["presentation"], "The disposed View did not close its presentation.");
console.log("GENERATED_RECONNECT_CLIENT_OK");
