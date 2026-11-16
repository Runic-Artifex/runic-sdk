// The View descriptions of the development build (src/describe.ts, #1041), which the bindings' DevTools labels show:
// a connected client names its view model, route, revision and presentations; the production build describes nothing.
import assert from "node:assert/strict";
import { test } from "node:test";
import * as productionBindings from "../dist/bindings.js";
import { createMockBridge as createProductionMockBridge, installMockBridge as installProductionMockBridge } from "../dist/mock.js";
import { connectView as connectProductionView } from "../dist/generated.js";
import { developmentBindings, developmentGenerated, developmentMock, developmentWire as bridgeWire } from "./support/development.js";

const contract = "Demo.Notes.NotesViewModel:ABC";
const members = { title: bridgeWire.string };

test("the development build describes a connected client until it is released", async () => {
  const bridge = developmentMock.createMockBridge();
  developmentMock.installMockBridge(bridge);
  const view = bridge.view("notes", { state: { title: "a" }, contract });
  const client = await developmentGenerated.connectView<{ title: string }>({ contract, route: "notes", members });
  const first = developmentBindings.describeViewClient(client);
  assert.deepEqual({ ...first, revision: undefined },
    { view: "NotesViewModel", route: "notes", contract, revision: undefined, presentations: 1, released: false });
  assert.equal(typeof first?.revision, "number");
  view.update({ title: "b" });
  await new Promise(resolve => setTimeout(resolve, 50));
  assert.ok(developmentBindings.describeViewClient(client)!.revision! > first!.revision!);
  client.dispose();
  assert.equal(developmentBindings.describeViewClient(client)?.released, true);
  // Anything else is not a client.
  for (const value of [undefined, null, 1, {}, { dispose() {} }]) assert.equal(developmentBindings.describeViewClient(value), undefined);
});

test("the production build describes no client", async () => {
  const bridge = createProductionMockBridge();
  installProductionMockBridge(bridge);
  bridge.view("notes", { state: { title: "a" }, contract });
  const client = await connectProductionView<{ title: string }>({ contract, route: "notes", members });
  assert.equal(productionBindings.describeViewClient(client), undefined);
  // A client of the production build carries no description for a development copy on the same page either.
  assert.equal(developmentBindings.describeViewClient(client), undefined);
  client.dispose();
});
