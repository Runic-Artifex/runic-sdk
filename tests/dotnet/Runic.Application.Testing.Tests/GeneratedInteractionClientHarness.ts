// The interaction surface of a generated client against its generated mock: handlers passed to connect{Name} are
// registered with open, so the producer can ask them at once; handle() replaces them; a stopped or disposed handler
// leaves the request unhandled, and a handler that throws fails it.
import assert from "node:assert/strict";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";

const [generatedDirectory] = Bun.argv.slice(2);
if (!generatedDirectory) throw new Error("Usage: GeneratedInteractionClientHarness.ts <generated-dir>");
const load = (name: string) => import(pathToFileURL(resolve(generatedDirectory, `${name}.ts`)).href);
const source = await Bun.file(resolve(generatedDirectory, "generatedInteraction.ts")).text();
const contract = /confirm: \{ contract: "([^"]+)"/.exec(source)?.[1];
assert.ok(contract, "The generated interaction contract was not emitted.");
const { connectGeneratedInteraction } = await load("generatedInteraction");
const { mockGeneratedInteraction } = await load("generatedInteraction.mock");
type Request = { readonly title: string; readonly attempt: number };

// Handlers given to connect are sent with open.
{
  const bridge = installMockBridge(createMockBridge());
  const mock = mockGeneratedInteraction(bridge, { state: { attempt: 0, acceptedCount: 0 } });
  const seen: Request[] = [];
  const client = await connectGeneratedInteraction({
    handlers: { confirm: (input: Request) => { seen.push(input); return input.attempt > 1; } },
  });
  const open = bridge.calls.find(call => call.kind === "open")?.message;
  assert.deepEqual(open?.["handlers"], [{ name: "confirm", contract }], "connect did not send its handlers with open.");
  assert.equal(mock.interactions.confirm.handled, true);
  assert.deepEqual(await mock.interactions.confirm.request({ title: "Delete?", attempt: 2 }), { kind: "answered", output: true });
  assert.deepEqual(seen, [{ title: "Delete?", attempt: 2 }], "The handler did not receive the decoded input.");

  // handle() replaces the handler; stopping it leaves requests unhandled.
  const stop = client.interactions.confirm.handle(() => false);
  assert.deepEqual(await mock.interactions.confirm.request({ title: "Again?", attempt: 3 }), { kind: "answered", output: false });
  stop();
  await bridge.flush();
  assert.deepEqual(await mock.interactions.confirm.request({ title: "Stopped?", attempt: 4 }), { kind: "unhandled" });

  // A handler that throws fails the request, and the page reports the error.
  const reported: unknown[] = [];
  const host = globalThis as { reportError?: (error: unknown) => void };
  const reportError = host.reportError;
  host.reportError = error => { reported.push(error); };
  client.interactions.confirm.handle(() => { throw new Error("The dialog failed."); });
  await bridge.flush();
  assert.equal((await mock.interactions.confirm.request({ title: "Broken?", attempt: 5 })).kind, "failed");
  host.reportError = reportError;
  assert.ok(reported.some(error => error instanceof Error && error.message === "The dialog failed."),
    "A throwing interaction handler was not reported.");
  client.dispose();
  await bridge.flush();
  assert.equal(mock.interactions.confirm.handled, false, "A disposed client kept its handler registered.");
}

// A client connected without handlers registers none until handle().
{
  const bridge = installMockBridge(createMockBridge());
  const mock = mockGeneratedInteraction(bridge, { state: { attempt: 0, acceptedCount: 0 } });
  const client = await connectGeneratedInteraction();
  assert.equal(bridge.calls.find(call => call.kind === "open")?.message["handlers"], undefined);
  assert.deepEqual(await mock.interactions.confirm.request({ title: "Nobody?", attempt: 1 }), { kind: "unhandled" });
  client.interactions.confirm.handle(() => true);
  await client.ask();
  assert.deepEqual(await mock.interactions.confirm.request({ title: "Now?", attempt: 2 }), { kind: "answered", output: true });
  client.dispose();
}

console.log("GENERATED_INTERACTION_CLIENT_OK");
