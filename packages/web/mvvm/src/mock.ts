import {
  CAPABILITIES,
  PROTOCOL_LIMITS,
  type ClientExecuteMessage,
  type ClientMessage,
  type ClientSetPropertyMessage,
  type FaultCode,
  type HostMessage,
  type JsonValue,
  type PatchChange,
  type Revision,
  type SnapshotMember,
} from "./protocol.js";
import {
  decodeUtf8,
  encodeUtf8,
  serializeJson,
  type FrameChannel,
  type FrameChannelObserver,
} from "./transport.js";
import { parseClientMessage } from "./validation.js";

const mockSession = "00000000-0000-4000-8000-000000000101";
const mockCapability = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
const persistentSession = Symbol("runic.toolkit.mvvm.mock.session");

interface MvvmMockSessionState {
  revision: Revision;
  readonly members: Map<string, SnapshotMember>;
}

type PersistentMvvmMockFixture = MvvmMockFixture & {
  readonly [persistentSession]: MvvmMockSessionState;
};

export interface MvvmMockMutation {
  readonly changes?: readonly PatchChange[];
  readonly result?: JsonValue;
  readonly fault?: Readonly<{
    code: FaultCode;
    message: string;
    retryable?: boolean;
  }>;
  readonly delayMilliseconds?: number;
}

export interface MvvmMockContext {
  readonly revision: Revision;
  readonly signal: AbortSignal;
  push(changes: readonly PatchChange[], delayMilliseconds?: number): Promise<void>;
}

export interface MvvmMockFixture {
  readonly contract: string;
  readonly initial: readonly SnapshotMember[];
  readonly latencyMilliseconds?: number;
  setProperty?(
    request: ClientSetPropertyMessage,
    context: MvvmMockContext,
  ): MvvmMockMutation | Promise<MvvmMockMutation>;
  execute?(
    request: ClientExecuteMessage,
    context: MvvmMockContext,
  ): MvvmMockMutation | Promise<MvvmMockMutation>;
}

export interface MvvmReplayStep {
  readonly kind: "setProperty" | "execute";
  readonly member: number;
  readonly mutation: MvvmMockMutation;
}

export interface MvvmReplayScript {
  readonly contract: string;
  readonly initial: readonly SnapshotMember[];
  /**
   * Sanitized semantic steps. Capability tokens, request/session IDs, raw
   * frames, property values, and command arguments never belong here.
   */
  readonly steps: readonly MvvmReplayStep[];
  readonly latencyMilliseconds?: number;
}

/**
 * Creates reconnectable channels over one fixture closure and authoritative
 * in-memory session. A replacement channel resumes accepted members and
 * revision instead of resetting the protocol behind a reconnecting client.
 */
export function createMvvmMockChannelFactory(
  fixture: Readonly<MvvmMockFixture>,
): () => MvvmMockFrameChannel {
  const persistent = Object.create(fixture) as PersistentMvvmMockFixture;
  Object.defineProperty(persistent, persistentSession, {
    value: createSessionState(fixture.initial),
    enumerable: false,
    configurable: false,
    writable: false,
  });
  return () => new MvvmMockFrameChannel(persistent);
}

/**
 * Creates a strict, single-use semantic replay fixture. The script matches
 * only operation kind and generated member ID, keeping recorded customer data
 * out of the checked-in artifact.
 */
export function createMvvmReplayFixture(
  script: Readonly<MvvmReplayScript>,
): MvvmMockFixture {
  let index = 0;
  const next = (
    kind: MvvmReplayStep["kind"],
    member: number,
  ): MvvmMockMutation => {
    const step = script.steps[index++];
    if (step === undefined) {
      return {
        fault: {
          code: "request.invalid",
          message: "The replay script has no remaining mutation.",
        },
      };
    }
    if (step.kind !== kind || step.member !== member) {
      return {
        fault: {
          code: "request.invalid",
          message:
            `Replay expected ${step.kind} member ${step.member}, ` +
            `not ${kind} member ${member}.`,
        },
      };
    }
    return structuredClone(step.mutation);
  };
  return {
    contract: script.contract,
    initial: script.initial,
    ...(script.latencyMilliseconds === undefined
      ? {}
      : { latencyMilliseconds: script.latencyMilliseconds }),
    setProperty: (request) =>
      next("setProperty", request.payload.member),
    execute: (request) =>
      next("execute", request.payload.member),
  };
}

/**
 * A development-only in-memory host that speaks the production wire protocol.
 * Framework code therefore uses its real generated contract and adapters.
 */
export class MvvmMockFrameChannel implements FrameChannel {
  public readonly mode = "mock" as const;
  private readonly fixture: MvvmMockFixture;
  private readonly session: MvvmMockSessionState;
  private readonly cancellation = new AbortController();
  private observer: FrameChannelObserver | undefined;
  private closed = false;

  public constructor(fixture: Readonly<MvvmMockFixture>) {
    if (fixture.contract.length === 0) throw new TypeError("A mock contract is required.");
    this.fixture = fixture;
    this.session = isPersistentFixture(fixture)
      ? fixture[persistentSession]
      : createSessionState(fixture.initial);
  }

  public subscribe(observer: FrameChannelObserver): () => void {
    if (this.observer !== undefined) {
      throw new Error("A mock channel supports one production transport subscriber.");
    }
    this.observer = observer;
    return () => {
      if (this.observer === observer) this.observer = undefined;
    };
  }

  public async send(frame: Uint8Array): Promise<void> {
    if (this.closed) throw new Error("The mock MVVM channel is closed.");
    const request = parseClientMessage(decodeUtf8(frame));
    await this.wait(this.fixture.latencyMilliseconds ?? 0);
    await this.dispatch(request);
  }

  public close(): void {
    if (this.closed) return;
    this.closed = true;
    this.cancellation.abort();
    const observer = this.observer;
    this.observer = undefined;
    observer?.close();
  }

  /** Emits a deterministic host-originated projection update. */
  public async push(
    changes: readonly PatchChange[],
    delayMilliseconds = 0,
  ): Promise<void> {
    if (changes.length === 0) return;
    await this.wait(delayMilliseconds);
    const fromRevision = this.session.revision;
    this.session.revision++;
    this.apply(changes);
    this.host({
      v: 1,
      kind: "patch",
      session: mockSession,
      view: this.currentView,
      payload: {
        fromRevision,
        toRevision: this.session.revision,
        changes,
      },
    });
  }

  /** Simulates physical loss; callers may then exercise normal reconnect policy. */
  public disconnect(cause: unknown = new Error("Mock disconnect")): void {
    this.observer?.close(cause);
  }

  private currentView = "00000000-0000-4000-8000-000000000102";

  private async dispatch(request: ClientMessage): Promise<void> {
    this.currentView = "view" in request ? request.view : this.currentView;
    switch (request.kind) {
      case "handshake":
        this.host({
          v: 1,
          kind: "handshakeResult",
          request: request.request,
          payload: {
            selectedVersion: 1,
            capabilities: CAPABILITIES,
            limits: {
              maxFrameBytes: PROTOCOL_LIMITS.maxFrameBytes,
              maxJsonDepth: PROTOCOL_LIMITS.maxJsonDepth,
              maxSessions: 1,
              maxPendingRequests: 64,
              maxSnapshotMembers: 4_096,
              maxPatchChanges: 1_024,
              maxCollectionItems: 10_000,
              commandTimeoutMilliseconds:
                PROTOCOL_LIMITS.defaultCommandTimeoutMilliseconds,
            },
          },
        });
        return;
      case "open":
        if (request.contract !== this.fixture.contract) {
          this.fault(request.request, "request.invalid", "The mock fixture contract does not match.");
          return;
        }
        this.host({
          v: 1,
          kind: "opened",
          contract: request.contract,
          session: mockSession,
          view: request.view,
          request: request.request,
          capability: mockCapability,
          payload: { snapshot: this.snapshot() },
        });
        return;
      case "setProperty":
        if (!this.acceptRevision(request)) return;
        await this.mutate(
          request,
          this.fixture.setProperty === undefined
            ? { changes: [{
                type: "property",
                member: request.payload.member,
                value: request.payload.value,
              }] }
            : await this.fixture.setProperty(request, this.context()),
          "setProperty",
        );
        return;
      case "execute":
        if (!this.acceptRevision(request)) return;
        await this.mutate(
          request,
          this.fixture.execute === undefined
            ? {}
            : await this.fixture.execute(request, this.context()),
          "execute",
        );
        return;
      case "requestSnapshot":
        this.host({
          v: 1,
          kind: "snapshot",
          session: mockSession,
          view: request.view,
          request: request.request,
          payload: this.snapshot(),
        });
        return;
      case "ack":
        this.host({
          v: 1,
          kind: "result",
          session: mockSession,
          view: request.view,
          request: request.request,
          payload: { operation: "ack", revision: this.session.revision },
        });
        return;
      case "cancel":
        this.host({
          v: 1,
          kind: "result",
          session: mockSession,
          view: request.view,
          request: request.request,
          payload: {
            operation: "cancel",
            revision: this.session.revision,
            targetRequest: request.payload.targetRequest,
            accepted: false,
          },
        });
        return;
      case "close":
        this.host({
          v: 1,
          kind: "closed",
          session: mockSession,
          view: request.view,
          request: request.request,
          payload: {
            revision: this.session.revision,
            reason: request.payload.reason ?? "Mock session closed",
          },
        });
        return;
    }
  }

  private context(): MvvmMockContext {
    return Object.freeze({
      revision: this.session.revision,
      signal: this.cancellation.signal,
      push: (changes: readonly PatchChange[], delay?: number) =>
        this.push(changes, delay),
    });
  }

  private acceptRevision(
    request: ClientSetPropertyMessage | ClientExecuteMessage,
  ): boolean {
    if (request.baseRevision === this.session.revision) return true;
    this.fault(
      request.request,
      "revision.stale",
      "The mock request used a stale revision.",
      true,
    );
    return false;
  }

  private async mutate(
    request: ClientSetPropertyMessage | ClientExecuteMessage,
    mutation: Readonly<MvvmMockMutation>,
    operation: "setProperty" | "execute",
  ): Promise<void> {
    await this.wait(mutation.delayMilliseconds ?? 0);
    if (mutation.fault !== undefined) {
      this.fault(
        request.request,
        mutation.fault.code,
        mutation.fault.message,
        mutation.fault.retryable ?? false,
      );
      return;
    }
    const changes = mutation.changes ?? [];
    if (changes.length > 0) await this.push(changes);
    this.host({
      v: 1,
      kind: "result",
      session: mockSession,
      view: request.view,
      request: request.request,
      payload: operation === "setProperty"
        ? { operation, revision: this.session.revision }
        : {
            operation,
            revision: this.session.revision,
            ...(mutation.result === undefined ? {} : { value: mutation.result }),
          },
    });
  }

  private fault(
    request: string,
    code: FaultCode,
    message: string,
    retryable = false,
  ): void {
    this.host({
      v: 1,
      kind: "fault",
      session: mockSession,
      view: this.currentView,
      request,
      payload: {
        code,
        message: sanitize(message),
        retryable,
        ...(code === "revision.stale"
          ? { currentRevision: this.session.revision, snapshotRequired: true }
          : {}),
      },
    });
  }

  private snapshot() {
    return {
      revision: this.session.revision,
      members: [...this.session.members.values()]
        .map((member) => structuredClone(member)),
    };
  }

  private apply(changes: readonly PatchChange[]): void {
    for (const change of changes) {
      if (change.type === "property" ||
          change.type === "command" ||
          change.type === "validation") {
        this.session.members.set(key(change), structuredClone(change));
        continue;
      }
      const existing = this.session.members.get(`collection:${change.member}`);
      if (existing?.type !== "collection") continue;
      const items = [...existing.items];
      if (change.type === "collectionMove") {
        const moved = items.splice(change.from, change.count);
        items.splice(change.to, 0, ...moved);
      } else if (change.operation === "reset") {
        items.splice(0, items.length, ...change.items);
      } else if (change.operation === "insert") {
        items.splice(change.index, 0, ...change.items);
      } else if (change.operation === "remove") {
        items.splice(change.index, change.items.length);
      } else {
        items.splice(change.index, change.items.length, ...change.items);
      }
      this.session.members.set(
        `collection:${change.member}`,
        { type: "collection", member: change.member, items },
      );
    }
  }

  private host(message: HostMessage): void {
    this.observer?.frame(encodeUtf8(serializeJson(message)));
  }

  private async wait(milliseconds: number): Promise<void> {
    if (milliseconds <= 0) return;
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(resolve, milliseconds);
      this.cancellation.signal.addEventListener("abort", () => {
        clearTimeout(timer);
        reject(this.cancellation.signal.reason);
      }, { once: true });
    });
  }
}

function createSessionState(
  initial: readonly SnapshotMember[],
): MvvmMockSessionState {
  const members = new Map<string, SnapshotMember>();
  for (const member of initial) {
    members.set(key(member), structuredClone(member));
  }
  return { revision: 0n, members };
}

function isPersistentFixture(
  fixture: Readonly<MvvmMockFixture>,
): fixture is PersistentMvvmMockFixture {
  return persistentSession in fixture;
}

function key(member: SnapshotMember | PatchChange): string {
  const type = member.type === "collectionMove" ? "collection" : member.type;
  return `${type}:${member.member}`;
}

function sanitize(message: string): string {
  const singleLine = message.replaceAll(/\s+/g, " ").trim();
  return singleLine.length <= 256 ? singleLine : singleLine.slice(0, 255) + "…";
}
