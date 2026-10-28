import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

/**
 * Runs the generated Toolkit client against the live ToolkitTypedViewModel bridge of GeneratedClientHarness.cs: direct
 * commands with string, DTO and null inputs, and operations whose terminal status .NET pushes to the watch.
 */
export async function runToolkitGeneratedClient(generatedDirectory: string): Promise<void> {
  const generated = await import(pathToFileURL(resolve(generatedDirectory, "toolkitTyped.ts")).href);
  const view = await generated.connectToolkitTyped();
  try {
    await view.rename("client string");
    await view.apply({ documentId: "document-1", delta: 4 });
    await view.optional(null);
    expect(view.snapshot.label === "client string" && view.snapshot.total === 4 && view.snapshot.optionalDocumentId === null,
      "Generated Toolkit command inputs did not reach .NET as sent.");

    const stringOperation = await view.startAsyncString("client async", { requestId: "toolkit-client-string" });
    const dtoOperation = await view.startAsyncDto({ documentId: "document-1", delta: 5 }, { requestId: "toolkit-client-dto" });
    const nullableOperation = await view.startAsyncOptional(null, { requestId: "toolkit-client-nullable" });
    const [stringCompletion, dtoCompletion, nullableCompletion] = await Promise.all([
      stringOperation.completion, dtoOperation.completion, nullableOperation.completion,
    ]);
    expect(stringCompletion.kind === "succeeded" && dtoCompletion.kind === "succeeded" && nullableCompletion.kind === "succeeded",
      "Generated Toolkit operations did not succeed.");
    expect(view.snapshot.total === 9, "A Toolkit operation's DTO input did not reach .NET.");
  } finally {
    view.dispose();
  }
}

function expect(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message);
}
