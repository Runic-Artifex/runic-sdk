import type {
  ClientMessage,
  HostMessage,
  MemberIdentifier,
  Revision,
  Uuid,
} from "./protocol.js";
import type {
  ProtocolTransport,
  TransportEvent,
  TransportState,
} from "./transport.js";

export type MvvmDevelopmentDirection = "client" | "host" | "runtime";

/** One bounded, payload-free event suitable for a development UI or test log. */
export interface MvvmDevelopmentEvent {
  readonly sequence: number;
  readonly at: number;
  readonly direction: MvvmDevelopmentDirection;
  readonly kind: string;
  readonly request?: Uuid;
  readonly contract?: string;
  readonly member?: MemberIdentifier;
  readonly memberName?: string;
  /** C# authoring member emitted by the contract generator, never a value. */
  readonly sourceMember?: string;
  readonly revision?: Revision;
  readonly bytes?: number;
  readonly durationMilliseconds?: number;
  readonly outcome?: string;
  readonly changeCount?: number;
  readonly previousState?: TransportState;
  readonly currentState?: TransportState;
}

export interface MvvmDevelopmentInspectorOptions {
  /** Number of sanitized events retained in memory. Defaults to 200. */
  readonly capacity?: number;
  /** Clock seam for deterministic tests. */
  readonly now?: () => number;
  /** Optional generated member metadata used for source-oriented labels. */
  readonly members?: readonly Readonly<MvvmDevelopmentMemberMetadata>[];
}

export interface MvvmDevelopmentMemberMetadata {
  readonly id: MemberIdentifier;
  readonly name: string;
  readonly sourceMember?: string;
}

export type MvvmDevelopmentListener =
  (event: Readonly<MvvmDevelopmentEvent>) => void;

/**
 * Correlates private-binding traffic without retaining capability tokens,
 * arguments, property values, validation text, raw frames, or exception data.
 */
export class MvvmDevelopmentInspector {
  private readonly capacity: number;
  private readonly now: () => number;
  private readonly retained: MvvmDevelopmentEvent[] = [];
  private readonly listeners = new Set<MvvmDevelopmentListener>();
  private readonly members = new Map<MemberIdentifier, MvvmDevelopmentMemberMetadata>();
  private readonly pending = new Map<Uuid, PendingInspection>();
  private sequence = 0;

  public constructor(options: Readonly<MvvmDevelopmentInspectorOptions> = {}) {
    const capacity = options.capacity ?? 200;
    if (!Number.isSafeInteger(capacity) || capacity < 1 || capacity > 10_000) {
      throw new RangeError("Inspector capacity must be between 1 and 10,000.");
    }
    this.capacity = capacity;
    this.now = options.now ?? (() => performance.now());
    for (const member of options.members ?? []) {
      if (!Number.isSafeInteger(member.id) || member.id <= 0) {
        throw new RangeError("Inspector member IDs must be positive integers.");
      }
      if (this.members.has(member.id)) {
        throw new TypeError(`Inspector member metadata duplicates ID ${member.id}.`);
      }
      this.members.set(member.id, Object.freeze({ ...member }));
    }
  }

  public get events(): readonly Readonly<MvvmDevelopmentEvent>[] {
    return this.retained;
  }

  public subscribe(listener: MvvmDevelopmentListener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  public clear(): void {
    this.retained.splice(0);
    this.pending.clear();
  }

  /** Attaches to one transport; disposal removes the observer and correlations. */
  public attach(transport: ProtocolTransport): () => void {
    const stop = transport.subscribe((event) => this.observe(event));
    return () => {
      stop();
      this.pending.clear();
    };
  }

  private observe(event: TransportEvent): void {
    const at = this.now();
    if (event.type === "send") {
      const outgoing = this.decorate(
        clientEvent(event.message, event.rawFrame.byteLength, at),
      );
      this.pending.set(event.message.request, {
        at,
        ...(outgoing.member === undefined ? {} : { member: outgoing.member }),
        ...(outgoing.memberName === undefined ? {} : { memberName: outgoing.memberName }),
        ...(outgoing.sourceMember === undefined ? {} : { sourceMember: outgoing.sourceMember }),
      });
      this.publish(outgoing);
      return;
    }
    if (event.type === "message") {
      const correlation = this.takeCorrelation(event.message, at);
      this.publish(hostEvent(
        event.message,
        event.rawFrame.byteLength,
        at,
        correlation,
      ));
      return;
    }
    if (event.type === "state") {
      this.publish({
        sequence: 0,
        at,
        direction: "runtime",
        kind: "connection",
        previousState: event.previous,
        currentState: event.current,
      });
      return;
    }
    this.publish({
      sequence: 0,
      at,
      direction: "runtime",
      kind: "protocolError",
      outcome: event.error.code,
    });
  }

  private takeCorrelation(
    message: HostMessage,
    at: number,
  ): CorrelatedInspection | undefined {
    if (!("request" in message)) return undefined;
    const started = this.pending.get(message.request);
    if (started === undefined) return undefined;
    this.pending.delete(message.request);
    return {
      ...started,
      durationMilliseconds: Math.max(0, at - started.at),
    };
  }

  private decorate(event: MvvmDevelopmentEvent): MvvmDevelopmentEvent {
    if (event.member === undefined) return event;
    const metadata = this.members.get(event.member);
    if (metadata === undefined) return event;
    return {
      ...event,
      memberName: metadata.name,
      ...(metadata.sourceMember === undefined
        ? {}
        : { sourceMember: metadata.sourceMember }),
    };
  }

  private publish(event: MvvmDevelopmentEvent): void {
    const accepted = Object.freeze({
      ...event,
      sequence: ++this.sequence,
    });
    this.retained.push(accepted);
    if (this.retained.length > this.capacity) this.retained.shift();
    for (const listener of [...this.listeners]) {
      try {
        listener(accepted);
      } catch {
        // Development tooling cannot interrupt the application transport.
      }
    }
  }
}

export interface MvvmInspectorOverlayOptions {
  readonly document?: Document;
  readonly title?: string;
}

/** Mounts a dependency-free, opt-in native-window inspector overlay. */
export function mountMvvmInspectorOverlay(
  inspector: MvvmDevelopmentInspector,
  options: Readonly<MvvmInspectorOverlayOptions> = {},
): () => void {
  const owner = options.document ?? globalThis.document;
  if (owner === undefined) {
    throw new Error("The inspector overlay requires a browser document.");
  }
  const details = owner.createElement("details");
  details.dataset.webuitoolkitInspector = "";
  details.style.cssText =
    "position:fixed;right:1rem;bottom:1rem;z-index:2147483647;width:min(42rem,calc(100vw - 2rem));" +
    "max-height:50vh;overflow:auto;color:#e9ecef;background:#161b22;border:1px solid #495057;" +
    "border-radius:.5rem;box-shadow:0 .5rem 2rem #0008;font:12px/1.45 ui-monospace,monospace";
  const summary = owner.createElement("summary");
  summary.textContent = options.title ?? "WebUIToolkit MVVM inspector";
  summary.style.cssText = "cursor:pointer;padding:.65rem .8rem;font-weight:700";
  const list = owner.createElement("ol");
  list.style.cssText = "list-style:none;margin:0;padding:0 .8rem .8rem";
  details.append(summary, list);
  owner.body.append(details);

  const append = (event: Readonly<MvvmDevelopmentEvent>): void => {
    const item = owner.createElement("li");
    item.style.cssText = "padding:.25rem 0;border-top:1px solid #30363d;overflow-wrap:anywhere";
    const duration = event.durationMilliseconds === undefined
      ? ""
      : ` ${event.durationMilliseconds.toFixed(1)}ms`;
    const member = event.member === undefined ? "" : ` member:${event.member}`;
    const source = event.sourceMember === undefined
      ? event.memberName === undefined ? "" : ` ${event.memberName}`
      : ` ${event.memberName ?? event.member} ← ${event.sourceMember}`;
    const revision = event.revision === undefined ? "" : ` r${event.revision}`;
    const bytes = event.bytes === undefined ? "" : ` ${event.bytes}B`;
    item.textContent =
      `#${event.sequence} ${event.direction} ${event.kind}${member}${source}${revision}${bytes}${duration}` +
      (event.outcome === undefined ? "" : ` ${event.outcome}`);
    list.prepend(item);
    while (list.childElementCount > 100) list.lastElementChild?.remove();
  };
  for (const event of inspector.events) append(event);
  const stop = inspector.subscribe(append);
  return () => {
    stop();
    details.remove();
  };
}

function clientEvent(
  message: ClientMessage,
  bytes: number,
  at: number,
): MvvmDevelopmentEvent {
  const member = "payload" in message && "member" in message.payload
    ? message.payload.member
    : undefined;
  return {
    sequence: 0,
    at,
    direction: "client",
    kind: message.kind,
    request: message.request,
    ...("contract" in message ? { contract: message.contract } : {}),
    ...(member === undefined ? {} : { member }),
    ...("baseRevision" in message ? { revision: message.baseRevision } : {}),
    bytes,
  };
}

function hostEvent(
  message: HostMessage,
  bytes: number,
  at: number,
  correlation: CorrelatedInspection | undefined,
): MvvmDevelopmentEvent {
  const revision = hostRevision(message);
  return {
    sequence: 0,
    at,
    direction: "host",
    kind: message.kind,
    ...("request" in message ? { request: message.request } : {}),
    ...("contract" in message ? { contract: message.contract } : {}),
    ...(correlation?.member === undefined ? {} : { member: correlation.member }),
    ...(correlation?.memberName === undefined
      ? {}
      : { memberName: correlation.memberName }),
    ...(correlation?.sourceMember === undefined
      ? {}
      : { sourceMember: correlation.sourceMember }),
    ...(revision === undefined ? {} : { revision }),
    bytes,
    ...(correlation === undefined
      ? {}
      : { durationMilliseconds: correlation.durationMilliseconds }),
    ...(message.kind === "fault" ? { outcome: message.payload.code } : {}),
    ...(message.kind === "patch"
      ? { changeCount: message.payload.changes.length }
      : message.kind === "snapshot"
        ? { changeCount: message.payload.members.length }
        : message.kind === "opened"
          ? { changeCount: message.payload.snapshot.members.length }
          : {}),
  };
}

interface PendingInspection {
  readonly at: number;
  readonly member?: MemberIdentifier;
  readonly memberName?: string;
  readonly sourceMember?: string;
}

interface CorrelatedInspection extends PendingInspection {
  readonly durationMilliseconds: number;
}

function hostRevision(message: HostMessage): Revision | undefined {
  switch (message.kind) {
    case "opened":
      return message.payload.snapshot.revision;
    case "snapshot":
      return message.payload.revision;
    case "patch":
      return message.payload.toRevision;
    case "result":
    case "closed":
      return message.payload.revision;
    case "fault":
      return message.payload.currentRevision;
    default:
      return undefined;
  }
}
