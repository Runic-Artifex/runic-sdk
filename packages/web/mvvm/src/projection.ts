import {
  type ClientSnapshot,
  type CommandInvocation,
  type CommandResult,
  MvvmClient,
  type MvvmClientEvent,
  type MvvmClientPhase,
  type MvvmFaultError,
  type CancelResult,
} from "./client.js";
import type { JsonValue, MemberIdentifier, Revision } from "./protocol.js";

/** An immutable, framework-neutral view of one accepted MVVM client state. */
export interface MvvmProjectionSnapshot {
  readonly phase: MvvmClientPhase;
  readonly synchronized: boolean;
  readonly revision: Revision | null;
  readonly properties: ReadonlyMap<MemberIdentifier, JsonValue>;
  readonly collections: ReadonlyMap<MemberIdentifier, readonly JsonValue[]>;
  readonly commands: ReadonlyMap<MemberIdentifier, Readonly<MvvmProjectedCommandState>>;
  readonly validation: ReadonlyMap<MemberIdentifier, readonly string[]>;
}

export interface MvvmProjectedCommandState {
  readonly canExecute: boolean;
  readonly isExecuting: boolean;
}

export type MvvmProjectionEvent =
  | { readonly type: "state"; readonly snapshot: MvvmProjectionSnapshot }
  | { readonly type: "fault"; readonly error: MvvmFaultError }
  | { readonly type: "protocolError"; readonly error: Error };

/**
 * A read-only projection over an {@link MvvmClient}. Member identifiers remain
 * protocol identifiers so an application can choose its own naming and binding
 * conventions without introducing a UI-framework dependency.
 */
export interface MvvmProjection {
  readonly snapshot: MvvmProjectionSnapshot;
  property(member: MemberIdentifier): JsonValue | undefined;
  collection(member: MemberIdentifier): readonly JsonValue[] | undefined;
  command(member: MemberIdentifier): Readonly<MvvmProjectedCommandState> | undefined;
  validation(member: MemberIdentifier): readonly string[] | undefined;
  subscribe(listener: (event: MvvmProjectionEvent) => void): () => void;
  setProperty(member: MemberIdentifier, value: JsonValue): Promise<{ readonly request: string; readonly revision: Revision }>;
  execute<T extends JsonValue = JsonValue>(member: MemberIdentifier, options?: Readonly<{ argument?: JsonValue }>): MvvmProjectedCommandInvocation<T>;
  dispose(): void;
}

export interface MvvmProjectedCommandInvocation<T extends JsonValue = JsonValue> {
  readonly request: string;
  readonly completion: Promise<CommandResult<T>>;
  cancel(): Promise<CancelResult>;
}

/** Creates a projection that emits exactly once for each client state notification. */
export function createMvvmProjection(client: MvvmClient): MvvmProjection {
  return new ClientProjection(client);
}

class ClientProjection implements MvvmProjection {
  private readonly listeners = new Set<(event: MvvmProjectionEvent) => void>();
  private readonly unsubscribeClient: () => void;
  private current: MvvmProjectionSnapshot;
  private disposed = false;

  public constructor(private readonly client: MvvmClient) {
    this.current = projectSnapshot(client.state);
    this.unsubscribeClient = client.subscribe((event) => this.onClientEvent(event));
  }

  public get snapshot(): MvvmProjectionSnapshot { return this.current; }
  public property(member: MemberIdentifier): JsonValue | undefined { return this.current.properties.get(member); }
  public collection(member: MemberIdentifier): readonly JsonValue[] | undefined { return this.current.collections.get(member); }
  public command(member: MemberIdentifier): Readonly<MvvmProjectedCommandState> | undefined { return this.current.commands.get(member); }
  public validation(member: MemberIdentifier): readonly string[] | undefined { return this.current.validation.get(member); }

  public subscribe(listener: (event: MvvmProjectionEvent) => void): () => void {
    this.assertActive();
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  public setProperty(member: MemberIdentifier, value: JsonValue) {
    this.assertActive();
    return this.client.setProperty(member, value);
  }

  public execute<T extends JsonValue = JsonValue>(
    member: MemberIdentifier,
    options: Readonly<{ argument?: JsonValue }> = {},
  ): MvvmProjectedCommandInvocation<T> {
    this.assertActive();
    const invocation: CommandInvocation<T> = this.client.execute<T>(member, options);
    return Object.freeze({ request: invocation.request, completion: invocation.completion, cancel: () => invocation.cancel() });
  }

  public dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.unsubscribeClient();
    this.listeners.clear();
  }

  private onClientEvent(event: MvvmClientEvent): void {
    if (this.disposed) return;
    let projected: MvvmProjectionEvent;
    if (event.type === "state") {
      this.current = projectSnapshot(event.snapshot);
      projected = { type: "state", snapshot: this.current };
    } else if (event.type === "fault") {
      projected = event;
    } else {
      projected = event;
    }
    for (const listener of [...this.listeners]) {
      try { listener(projected); } catch { /* View subscribers cannot affect protocol dispatch. */ }
    }
  }

  private assertActive(): void {
    if (this.disposed) throw new Error("The MVVM projection has been disposed.");
  }
}

function projectSnapshot(snapshot: ClientSnapshot): MvvmProjectionSnapshot {
  return Object.freeze({
    phase: snapshot.phase,
    synchronized: snapshot.synchronized,
    revision: snapshot.revision,
    properties: readonlyMap(snapshot.properties, cloneJson),
    collections: readonlyMap(snapshot.collections, (value) => Object.freeze(value.map(cloneJson))),
    commands: readonlyMap(snapshot.commands, (value) => Object.freeze({ canExecute: value.canExecute, isExecuting: value.isExecuting })),
    validation: readonlyMap(snapshot.validation, (value) => Object.freeze([...value])),
  });
}

function readonlyMap<T>(source: ReadonlyMap<MemberIdentifier, T>, copy: (value: T) => T): ReadonlyMap<MemberIdentifier, T> {
  const map = new Map<MemberIdentifier, T>();
  for (const [member, value] of source) map.set(member, copy(value));
  // Map has mutation methods at runtime. A proxy keeps the public ReadonlyMap
  // contract honest without changing iteration or lookup semantics.
  return new Proxy(map, {
    get(target, property, receiver) {
      if (property === "set" || property === "delete" || property === "clear") {
        return () => { throw new TypeError("Projected maps are immutable."); };
      }
      const value = Reflect.get(target, property, target);
      return typeof value === "function" ? value.bind(target) : value;
    },
  }) as ReadonlyMap<MemberIdentifier, T>;
}

function cloneJson(value: JsonValue): JsonValue {
  if (value === null || typeof value !== "object") return value;
  if (Array.isArray(value)) return Object.freeze(value.map(cloneJson));
  const copy: Record<string, JsonValue> = {};
  for (const [key, child] of Object.entries(value)) copy[key] = cloneJson(child);
  return Object.freeze(copy);
}
