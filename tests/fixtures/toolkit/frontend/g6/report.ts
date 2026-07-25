export const mandatoryFixtures = Object.freeze([
  "g6.protocol-identity",
  "g6.snapshot-atomicity",
  "g6.successful-mutation",
  "g6.command-result",
  "g6.validation",
  "g6.reconnect-snapshot",
  "g6.directive-lifecycle",
  "g6.subscriber-isolation",
  "g6.hostile-text",
  "g6.core-vertical",
]);

export interface BrowserResult {
  readonly adapter: "angular";
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
  document.documentElement.dataset.g6Status = result.status;
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
