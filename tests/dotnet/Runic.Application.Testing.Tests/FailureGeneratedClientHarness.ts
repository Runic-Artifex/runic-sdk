import { isBridgeOutcome } from "@runic-artifex/views";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import type { LiveHost } from "./GeneratedLiveBridge.ts";

/** Runs the declared failures (W130-029) of the live FailureToolkitViewModel bridge's commands: each resolves a BridgeOutcome. */
export async function runFailureGeneratedClient(generatedDirectory: string): Promise<void> {
  const generated = await import(pathToFileURL(resolve(generatedDirectory, "failureToolkit.ts")).href);
  const view = await generated.connectFailureToolkit();
  try {
    const saved = await view.save();
    expect(isBridgeOutcome(saved) && !saved.ok && saved.failure.$case === "titleRequired",
      "save() did not resolve its declared failure.");
    const reserved = await view.reserve(5);
    expect(!reserved.ok && reserved.failure.limit === 3 && reserved.failure.requested === 5,
      "reserve() did not decode its DTO failure.");
  } finally {
    view.dispose();
  }
}

/** Runs a declared operation failure of the live bridge: outcome() resolves the decoded failure. */
export async function runFailureGeneratedOperations(live: LiveHost, generatedDirectory: string): Promise<void> {
  const generated = await import(pathToFileURL(resolve(generatedDirectory, "failureToolkit.ts")).href);
  const view = await generated.connectFailureToolkit();
  try {
    await live.step("failureToolkit.archive");
    const operation = await view.startPublish({ requestId: "failure-client-publish" });
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
