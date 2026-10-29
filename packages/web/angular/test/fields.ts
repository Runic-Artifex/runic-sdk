// Fake Views for the field tests: a client whose state changes when a write settles.
import { signal, type WritableSignal } from "@angular/core";
import type { FieldBaseline, FieldWriteReceipt, ViewClient } from "@runic-artifex/views";

export type EditorState = {
  readonly title: string;
  readonly count: number;
  readonly done: boolean;
  readonly titleErrors: readonly string[];
};

export class FakeEditor implements ViewClient<EditorState> {
  snapshot: EditorState;
  version = 1;
  readonly written: unknown[] = [];
  private readonly listeners = new Set<(state: EditorState) => void>();

  constructor(state: Partial<EditorState> = {}) {
    this.snapshot = { title: "", count: 0, done: false, titleErrors: [], ...state };
  }

  subscribe(listener: (state: EditorState) => void): () => void {
    this.listeners.add(listener);
    listener(this.snapshot);
    return () => { this.listeners.delete(listener); };
  }

  dispose(): void { this.listeners.clear(); }

  /** Changes the state, as the producer does. */
  apply(patch: Partial<EditorState>): void {
    this.snapshot = { ...this.snapshot, ...patch };
    this.version++;
    for (const listener of [...this.listeners]) listener(this.snapshot);
  }

  /** A setter: settles a microtask later, then the state has the value (titles are trimmed). */
  async set<K extends "title" | "count" | "done">(name: K, value: EditorState[K]): Promise<void> {
    this.written.push(value);
    await Promise.resolve();
    this.apply({ [name]: typeof value === "string" ? value.trim() : value } as Partial<EditorState>);
  }
}

/** An `injectView`-like pair of signals following a client. */
export function viewOf(client: FakeEditor): {
  readonly state: WritableSignal<EditorState | undefined>;
  readonly client: WritableSignal<FakeEditor | undefined>;
} {
  const view = { state: signal<EditorState | undefined>(client.snapshot), client: signal<FakeEditor | undefined>(client) };
  client.subscribe(state => view.state.set(state));
  return view;
}

/** Field definitions of the shape the generator emits as `{View}Fields`. */
export const Fields = {
  title: {
    name: "title", kind: "text" as const,
    read: (state: EditorState) => state.title,
    write: (client: FakeEditor, value: string) => client.set("title", value),
    errors: (state: EditorState) => state.titleErrors,
  },
  count: {
    name: "count", kind: "number" as const, integer: true,
    read: (state: EditorState) => state.count,
    write: (client: FakeEditor, value: number) => client.set("count", value),
  },
  done: {
    name: "done", kind: "boolean" as const,
    read: (state: EditorState) => state.done,
    write: (client: FakeEditor, value: boolean) => client.set("done", value),
  },
};

/** A checked title whose checked write answers with `receipt`. */
export function checkedTitle(receipt: (value: string) => FieldWriteReceipt<string>) {
  return {
    ...Fields.title,
    checked: async (client: FakeEditor, value: string) => { client.written.push(value); return receipt(value); },
    baseline: (client: FakeEditor): FieldBaseline<string> => ({ value: client.snapshot.title, version: client.version }),
  };
}

/** Lets settled writes and their microtasks run. */
export async function settle(): Promise<void> {
  for (let round = 0; round < 10; round++) await Promise.resolve();
}
