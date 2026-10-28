// Runs the operations and interactions of generated clients against the real generated bridges of
// GeneratedClientHarness.cs over Views protocol 4: typed operation results, Toolkit operation inputs, declared operation
// failures, and an interaction answered by a handler passed to connect.
import { runToolkitGeneratedOperations } from "./ToolkitGeneratedClientHarness.ts";
import { runFailureGeneratedOperations } from "./FailureGeneratedClientHarness.ts";
import { installLiveBridge } from "./GeneratedLiveBridge.ts";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const [generatedDirectory] = Bun.argv.slice(2);
if (!generatedDirectory) throw new Error("Usage: GeneratedOperationClientHarness.ts <generated-dir>");
const live = installLiveBridge();
const load = (name: string) => import(pathToFileURL(resolve(generatedDirectory, `${name}.ts`)).href);

// Operations: the typed result decodes from .NET's terminal status, and another member's id is unknown.
const typedReactive = await load("typedReactive");
const typed = await typedReactive.connectTypedReactive();
const operation = await typed.startSave({ documentId: "document-1", content: "from-client", expectedVersion: 7 },
  { requestId: "generated-client-save" });
await live.step("typedReactive.releaseSave");
const completion = await operation.completion;
expect(completion.kind === "succeeded" && completion.result?.documentId === "document-1"
  && completion.result.savedVersion === 8 && completion.result.contentLength === "from-client".length,
  "A typed operation result did not decode from the C# terminal status.");
await expectThrows(() => typed.recoverLastResult("generated-client-save"),
  "A recovery of an operation id started for another member was accepted.");
typed.dispose();

// An interaction handler passed to connect answers .NET with the DTO it accepts.
const dtoInteraction = await (await load("dtoInteraction")).connectDtoInteraction({
  handlers: { chooseEntry: (input: string) => ({ name: input === "Pick an entry" ? "picked" : input, count: 6 }) },
});
await dtoInteraction.pick();
expect(dtoInteraction.snapshot.picked?.name === "picked", "The interaction's DTO output did not reach the ViewModel.");
dtoInteraction.dispose();
await live.step("dtoInteraction.expectPicked");

await runToolkitGeneratedOperations(generatedDirectory);
await runFailureGeneratedOperations(live, generatedDirectory);

await live.close();
console.log("GENERATED_OPERATION_CLIENT_OK");

async function expectThrows(action: () => Promise<unknown>, message: string): Promise<void> {
  try { await action(); }
  catch { return; }
  throw new Error(message);
}

function expect(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message);
}
