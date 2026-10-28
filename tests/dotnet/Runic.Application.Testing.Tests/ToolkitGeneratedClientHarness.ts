import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

/** Runs the generated Toolkit client's direct commands with string, DTO and null inputs against the live bridge. */
export async function runToolkitGeneratedClient(generatedDirectory: string): Promise<void> {
  const generated = await import(pathToFileURL(resolve(generatedDirectory, "toolkitTyped.ts")).href);
  const view = await generated.connectToolkitTyped();
  try {
    await view.rename("client string");
    await view.apply({ documentId: "document-1", delta: 4 });
    await view.optional(null);
    expect(view.snapshot.label === "client string" && view.snapshot.total === 4 && view.snapshot.optionalDocumentId === null,
      "Generated Toolkit command inputs did not reach .NET as sent.");
  } finally {
    view.dispose();
  }
}

/** Runs the generated Toolkit client's operations, whose terminal status .NET pushes to the watch. */
export async function runToolkitGeneratedOperations(generatedDirectory: string): Promise<void> {
  const generated = await import(pathToFileURL(resolve(generatedDirectory, "toolkitTyped.ts")).href);
  const view = await generated.connectToolkitTyped();
  try {
    const total = view.snapshot.total;
    const stringOperation = await view.startAsyncString("client async", { requestId: "toolkit-client-string" });
    const dtoOperation = await view.startAsyncDto({ documentId: "document-1", delta: 5 }, { requestId: "toolkit-client-dto" });
    const nullableOperation = await view.startAsyncOptional(null, { requestId: "toolkit-client-nullable" });
    const [stringCompletion, dtoCompletion, nullableCompletion] = await Promise.all([
      stringOperation.completion, dtoOperation.completion, nullableOperation.completion,
    ]);
    expect(stringCompletion.kind === "succeeded" && dtoCompletion.kind === "succeeded" && nullableCompletion.kind === "succeeded",
      "Generated Toolkit operations did not succeed.");
    expect(view.snapshot.total === total + 5, "A Toolkit operation's DTO input did not reach .NET.");
  } finally {
    view.dispose();
  }
}

function expect(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message);
}
