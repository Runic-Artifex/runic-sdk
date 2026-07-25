export const mandatoryFixtures = Object.freeze([
  "g5.protocol-identity",
  "g5.snapshot-atomicity",
  "g5.successful-mutation",
  "g5.command-result",
  "g5.validation",
  "g5.reconnect-snapshot",
  "g5.lifecycle-cleanup",
  "g5.subscriber-isolation",
  "g5.hostile-text",
  "g5.core-vertical",
]);

export interface BrowserResult {
  readonly adapter: "react" | "vue" | "svelte";
  readonly status: "passed" | "failed";
  readonly fixtures: readonly string[];
  readonly amount?: number;
  readonly submissions?: number;
  readonly commits?: number;
  readonly listenerCount?: number;
  readonly error?: string;
}

export function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message);
}

export async function report(result: BrowserResult): Promise<void> {
  document.documentElement.dataset.g5Status = result.status;
  document.body.textContent = JSON.stringify(result);
  const response = await fetch("/result", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(result),
  });
  if (!response.ok) throw new Error(`Result endpoint returned ${response.status}.`);
}

export function commandSubmissions(value: unknown): number {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("Command result must be an object.");
  }
  const submissions = (value as { readonly submissions?: unknown }).submissions;
  if (typeof submissions !== "number") throw new Error("Command result submissions must be numeric.");
  return submissions;
}
