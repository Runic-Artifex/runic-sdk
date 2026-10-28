// Reads a real NamingViewModel state through the generated client (CodegenShapeTests). Wire names that are special on
// JavaScript objects must become own state properties: "__proto__" must not replace the state's prototype.
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { installLiveBridge } from "./GeneratedLiveBridge.ts";

const [generatedDirectory] = Bun.argv.slice(2);
if (!generatedDirectory) throw new Error("Usage: GeneratedSpecialNamesHarness.ts <generated-dir>");
const live = installLiveBridge();
const naming = await import(pathToFileURL(resolve(generatedDirectory, "naming.ts")).href);
const view = await naming.connectNaming();
const state = view.snapshot as Record<string, unknown>;
expect(Object.getPrototypeOf(state) === Object.prototype, "The \"__proto__\" wire name replaced the state's prototype.");
for (const [name, value] of [["__proto__", "proto"], ["constructor", "constructor"], ["prototype", "prototype"]])
  expect(Object.prototype.hasOwnProperty.call(state, name) && state[name] === value,
    `The "${name}" wire name was not read as an own state property.`);
expect(state.nothing === null, "A null case-less enum was not read.");
view.dispose();
await live.close();
console.log("GENERATED_SPECIAL_NAMES_OK");

function expect(condition: boolean, message: string): asserts condition {
  if (!condition) throw new Error(message);
}
