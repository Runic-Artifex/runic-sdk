import { isBridgeOutcome } from "@runic-artifex/views";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

export type FailureGeneratedClientTranscript = {
  readonly snapshot: string;
  readonly saveReply: string;
  readonly reserveRequest: string;
  readonly reserveReply: string;
  readonly publishRequestId: string;
  readonly publishAdmission: string;
  readonly publishWaitRequest: string;
  readonly publishCompletion: string;
};

/**
 * Replays recorded generated-bridge replies for declared failures through the
 * generated client: commands resolve a BridgeOutcome and operations resolve
 * outcome() with the decoded failure.
 */
export async function runFailureGeneratedClient(fixture: FailureGeneratedClientTranscript, generatedDirectory: string): Promise<void> {
  const host = globalThis as typeof globalThis & { window?: Record<string, unknown> };
  host.window = host as unknown as Record<string, unknown>;
  (host.window as Record<string, unknown>).__runicBridge = {
    isConnected: () => true,
    async call(route: string, ...args: unknown[]) {
      switch (route) {
        case "failureToolkitSnapshot": return fixture.snapshot;
        case "failureToolkitSave": return fixture.saveReply;
        case "failureToolkitReserve": expect(args[0] === fixture.reserveRequest, "reserve() did not send its input."); return fixture.reserveReply;
        case "failureToolkitStartPublish": expect(args[0] === fixture.publishRequestId, "startPublish did not send its request id."); return fixture.publishAdmission;
        case "__runicOperationWait": expect(args[0] === fixture.publishWaitRequest, "Unexpected wait payload."); return fixture.publishCompletion;
        default: throw new Error(`Unexpected failure generated-client route ${route}.`);
      }
    },
  };
  const generated = await import(pathToFileURL(resolve(generatedDirectory, "failureToolkit.ts")).href);
  const view = await generated.connectFailureToolkit();
  try {
    const saved = await view.save();
    expect(isBridgeOutcome(saved) && !saved.ok && saved.failure.$case === "titleRequired",
      "save() did not resolve its declared failure.");
    const reserved = await view.reserve(5);
    expect(!reserved.ok && reserved.failure.limit === 3 && reserved.failure.requested === 5,
      "reserve() did not decode its DTO failure.");
    const operation = await view.startPublishWithRequestId(fixture.publishRequestId);
    const outcome = await operation.outcome();
    expect(!outcome.ok && outcome.failure === "Archived", "outcome() did not decode the enum failure.");
    const status = await operation.wait();
    expect(status.kind === "domain-failed" && status.failure === "Archived", "wait() did not report the domain-failed status.");
  } finally {
    view.dispose();
  }
}

function expect(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message);
}
