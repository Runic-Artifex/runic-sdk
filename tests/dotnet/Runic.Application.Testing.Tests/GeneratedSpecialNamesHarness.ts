// Hydrates a real NamingViewModel snapshot through the generated client.
// Wire names that are special on JavaScript objects must become own state
// properties: "__proto__" must not replace the state's prototype.
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const [snapshotPath, generatedDirectory] = Bun.argv.slice(2);
if (!snapshotPath || !generatedDirectory) throw new Error("Usage: GeneratedSpecialNamesHarness.ts <snapshot.json> <generated-dir>");
const reply = readFileSync(snapshotPath, "utf8");
const host = globalThis as typeof globalThis & { window?: Record<string, unknown> };
host.window = host as unknown as Record<string, unknown>;
host.window.__runicBridge = {
  isConnected: () => true,
  onReconnect: () => () => {},
  async call(route: string) {
    if (route === "namingSnapshot") return reply;
    throw new Error(`Unexpected generated-client route ${route}.`);
  },
};

const naming = await import(pathToFileURL(resolve(generatedDirectory, "naming.ts")).href);
const view = await naming.connectNaming();
const state = view.snapshot as Record<string, unknown>;
expect(Object.getPrototypeOf(state) === Object.prototype, "The \"__proto__\" wire name replaced the state's prototype.");
for (const [name, value] of [["__proto__", "proto"], ["constructor", "constructor"], ["prototype", "prototype"]])
  expect(Object.prototype.hasOwnProperty.call(state, name) && state[name] === value,
    `The "${name}" wire name was not hydrated as an own state property.`);
expect(state.nothing === null, "A null case-less enum was not hydrated.");
view.dispose();
console.log("GENERATED_SPECIAL_NAMES_OK");

function expect(condition: boolean, message: string): asserts condition {
  if (!condition) throw new Error(message);
}
