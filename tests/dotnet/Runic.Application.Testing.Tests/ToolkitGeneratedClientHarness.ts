import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

type OperationTranscript = {
  readonly requestId: string;
  readonly startRequest: string;
  readonly admission: string;
  readonly waitRequest: string;
  readonly completion: string;
};

export type ToolkitGeneratedClientTranscript = {
  readonly snapshot: string;
  readonly stringRequest: string;
  readonly stringReply: string;
  readonly dtoRequest: string;
  readonly dtoReply: string;
  readonly nullableRequest: string;
  readonly nullableReply: string;
  readonly asyncString: OperationTranscript;
  readonly asyncDto: OperationTranscript;
  readonly asyncNullable: OperationTranscript;
};

type Call = { readonly route: string; readonly args: readonly unknown[] };
type Bridge = { isConnected(): boolean; call(route: string, ...args: unknown[]): Promise<string> };

/**
 * Runs one finite transcript through the actual generated TypeScript client.
 * The fake has no queues and no polling endpoints: each expected route returns
 * exactly one recorded C# response, which keeps the harness bounded by design.
 */
export async function runToolkitGeneratedClient(
  fixture: ToolkitGeneratedClientTranscript, generatedDirectory: string): Promise<void> {
  const calls: Call[] = [];
  const host = globalThis as typeof globalThis & { window?: Record<string, unknown> };
  host.window = host as unknown as Record<string, unknown>;
  const bridge: Bridge = {
    isConnected: () => true,
    async call(route, ...args) {
      calls.push({ route, args });
      switch (route) {
        case "toolkitTypedSnapshot": return fixture.snapshot;
        case "toolkitTypedRename": expectArgs(route, args, fixture.stringRequest); return fixture.stringReply;
        case "toolkitTypedApply": expectArgs(route, args, fixture.dtoRequest); return fixture.dtoReply;
        case "toolkitTypedOptional": expectArgs(route, args, fixture.nullableRequest); return fixture.nullableReply;
        case "toolkitTypedStartAsyncString": expectArgs(route, args, fixture.asyncString.startRequest); return fixture.asyncString.admission;
        case "toolkitTypedStartAsyncDto": expectArgs(route, args, fixture.asyncDto.startRequest); return fixture.asyncDto.admission;
        case "toolkitTypedStartAsyncOptional": expectArgs(route, args, fixture.asyncNullable.startRequest); return fixture.asyncNullable.admission;
        case "__runicOperationWait": {
          const expected = [fixture.asyncString, fixture.asyncDto, fixture.asyncNullable]
            .find(operation => operation.waitRequest === args[0]);
          if (!expected) throw new Error("Unexpected Toolkit operation wait payload.");
          return expected.completion;
        }
        default: throw new Error(`Unexpected Toolkit generated-client route ${route}.`);
      }
    },
  };
  (host.window as Record<string, unknown>).__runicBridge = bridge;

  const generated = await import(pathToFileURL(resolve(generatedDirectory, "toolkitTyped.ts")).href);
  const view = await generated.connectToolkitTyped();
  try {
    await view.rename("client string");
    await view.apply({ documentId: "document-1", delta: 4 });
    await view.optional(null);
    expect(view.snapshot.label === "client string" && view.snapshot.total === 4
      && view.snapshot.optionalDocumentId === null,
      "Generated Toolkit direct command clients did not hydrate their C# replies.");

    const stringOperation = await view.startAsyncStringWithRequestId(fixture.asyncString.requestId, "client async");
    const dtoOperation = await view.startAsyncDtoWithRequestId(fixture.asyncDto.requestId,
      { documentId: "document-1", delta: 5 });
    const nullableOperation = await view.startAsyncOptionalWithRequestId(fixture.asyncNullable.requestId, null);
    const [stringCompletion, dtoCompletion, nullableCompletion] = await Promise.all([
      stringOperation.completion, dtoOperation.completion, nullableOperation.completion,
    ]);
    expect(stringCompletion.kind === "succeeded" && dtoCompletion.kind === "succeeded"
      && nullableCompletion.kind === "succeeded",
      "Generated Toolkit operation clients did not accept the recorded terminal replies.");
    expect(calls.filter(call => call.route === "__runicOperationWait").length === 3,
      "Toolkit operation completion did not issue one finite wait per operation.");
  } finally {
    view.dispose();
  }
}

function expectArgs(route: string, actual: readonly unknown[], expected: string): void {
  expect(actual.length === 1 && actual[0] === expected,
    `${route} did not preserve the generated C# request payload.`);
}

function expect(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message);
}
