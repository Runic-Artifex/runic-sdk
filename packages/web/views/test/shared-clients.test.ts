// Shared clients (`[RunicSharedClient]`, #839): the type-only marker of generated page references and one registry entry
// per kind of a shared reference.
import assert from "node:assert/strict";
import { test } from "node:test";
import { registryEntries, sharedPageReferences, type SharedPageReferences, type ViewRegistryPage } from "../dist/index.js";

type ResultPage = { readonly kind: "orders" | "invoices"; connect(): Promise<{ readonly title: string }> };
type Orders = { readonly kind: "orders"; connect(): Promise<{ readonly title: string; readonly total: number }>; readonly [sharedPageReferences]?: ResultPage };
type Settings = { readonly kind: "settings"; connect(): Promise<unknown> };

// The entry for a kind accepts the kind's reference and the shared references it names, and nothing more otherwise.
const ordersPages: [ViewRegistryPage<Orders | Settings | null, "orders">] extends [Orders | ResultPage] ? true : false = true;
const settingsPages: [ViewRegistryPage<Orders | Settings, "settings">] extends [Settings] ? true : false = true;
const noShared: [SharedPageReferences<Settings>] extends [never] ? true : false = true;

test("registryEntries maps every kind to the component", () => {
  const component = () => null;
  const entries = registryEntries(["orders", "invoices"], component);
  assert.deepEqual(Object.keys(entries), ["orders", "invoices"]);
  assert.equal(entries.orders, component);
  assert.equal(entries.invoices, component);
  assert.deepEqual(registryEntries([], component), {});
});

test("sharedPageReferences is a symbol that no reference carries at run time", () => {
  assert.equal(typeof sharedPageReferences, "symbol");
  assert.ok(ordersPages && settingsPages && noShared);
});
