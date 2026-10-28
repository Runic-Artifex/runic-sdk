// The page bridge of a generated-client harness that GeneratedClientLiveHost.cs runs: requests and steps go to the
// .NET test as marked lines on standard output, and its replies and pushes come back as lines on standard input. The
// generated clients therefore talk Views protocol 4 to real generated bridges.
import type { RunicBridgeClient } from "@runic-artifex/views";

const marker = "\u0001runic-live ";

type Answer = { readonly id?: number; readonly reply?: string; readonly error?: string; readonly push?: string };

export interface LiveHost {
  /** Runs a named model change or check of the .NET test; it rejects when the step throws. */
  step(name: string): Promise<void>;
  /** The requests the page sent, parsed, in order. */
  readonly requests: readonly Record<string, unknown>[];
  /** Ends the run once the .NET test closed standard input. */
  close(): Promise<void>;
}

/** Installs `window.__runicBridge` as the live bridge. Call once per script. */
export function installLiveBridge(): LiveHost {
  let next = 1;
  const pending = new Map<number, { resolve(reply: string): void; reject(error: Error): void }>();
  const listeners = new Set<(message: string) => void>();
  const requests: Record<string, unknown>[] = [];
  const send = (message: object) => { process.stdout.write(`${marker}${JSON.stringify(message)}\n`); };
  const ask = (message: object) => new Promise<string>((resolve, reject) => {
    const id = next++;
    pending.set(id, { resolve, reject });
    send({ id, ...message });
  });
  const reading = (async () => {
    for await (const line of console) {
      if (line.trim() === "") continue;
      const answer = JSON.parse(line) as Answer;
      if (answer.push !== undefined) {
        for (const listener of [...listeners]) listener(answer.push);
        continue;
      }
      const waiter = pending.get(answer.id ?? -1);
      pending.delete(answer.id ?? -1);
      if (answer.error !== undefined) waiter?.reject(new Error(answer.error));
      else waiter?.resolve(answer.reply ?? "");
    }
  })();
  const bridge: RunicBridgeClient = {
    isConnected: () => true,
    request: message => {
      requests.push(JSON.parse(message) as Record<string, unknown>);
      return ask({ request: message });
    },
    onMessage: listener => {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    onConnectionChange: listener => {
      listener(true);
      return () => {};
    },
  };
  const host = globalThis as typeof globalThis & { window?: Record<string, unknown> };
  host.window = host as unknown as Record<string, unknown>;
  host.window["__runicBridge"] = bridge;
  return {
    async step(name) { await ask({ step: name }); },
    requests,
    async close() {
      send({ done: true });
      await reading;
    },
  };
}
