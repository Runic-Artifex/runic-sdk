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
  /** Project-relative authoring location emitted by a C#-first contract. */
  readonly source?: Readonly<MvvmDevelopmentSourceLocation>;
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
  readonly source?: Readonly<MvvmDevelopmentSourceLocation>;
}

export interface MvvmDevelopmentSourceLocation {
  readonly file: string;
  readonly line: number;
  readonly column: number;
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
      if (member.source !== undefined) validateSourceLocation(member.source);
      this.members.set(member.id, Object.freeze({
        ...member,
        ...(member.source === undefined
          ? {}
          : { source: Object.freeze({ ...member.source }) }),
      }));
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
        ...(outgoing.source === undefined ? {} : { source: outgoing.source }),
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
      ...(metadata.source === undefined ? {} : { source: metadata.source }),
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
  /** Called when a source link is activated. Defaults to copying `file:line:column`. */
  readonly openSource?: (
    source: Readonly<MvvmDevelopmentSourceLocation>,
  ) => void | Promise<void>;
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
    const label =
      `#${event.sequence} ${event.direction} ${event.kind}${member}${source}${revision}${bytes}${duration}` +
      (event.outcome === undefined ? "" : ` ${event.outcome}`);
    item.append(owner.createTextNode(label));
    if (event.source !== undefined) {
      const link = owner.createElement("a");
      link.href = "#";
      link.textContent =
        ` ${event.source.file}:${event.source.line}:${event.source.column}`;
      link.title = "Open or copy the C# authoring location";
      link.style.cssText = "color:#79c0ff;margin-left:.35rem";
      link.addEventListener("click", event_ => {
        event_.preventDefault();
        const action = options.openSource ?? copySourceLocation;
        void action(event.source!);
      });
      item.append(link);
    }
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

export interface MvvmInspectorEndpointReporterOptions {
  readonly fetch?: typeof globalThis.fetch;
}

export interface InjectedMvvmDevelopmentTools {
  readonly inspector: MvvmDevelopmentInspector;
  dispose(): void;
}

/**
 * Activates the overlay and terminal reporter only when the coordinated
 * development bootstrap injected its random loopback endpoint.
 */
export function createInjectedMvvmDevelopmentTools(
  members: readonly Readonly<MvvmDevelopmentMemberMetadata>[],
): InjectedMvvmDevelopmentTools | undefined {
  const injected = (
    globalThis as typeof globalThis & {
      __webuitoolkitMvvmDevelopment?: unknown;
    }
  ).__webuitoolkitMvvmDevelopment;
  if (injected === null || typeof injected !== "object") return undefined;
  const endpointValue = Reflect.get(injected, "endpoint");
  if (typeof endpointValue !== "string") return undefined;
  const endpoint = new URL(endpointValue);
  if (endpoint.protocol !== "http:" ||
      !["127.0.0.1", "localhost", "[::1]"].includes(endpoint.hostname)) {
    throw new Error("The injected MVVM inspector endpoint must be loopback HTTP.");
  }

  const inspector = new MvvmDevelopmentInspector({ members });
  const stopEndpoint = reportMvvmInspectorToEndpoint(inspector, endpoint);
  const stopOverlay = globalThis.document === undefined
    ? undefined
    : mountMvvmInspectorOverlay(inspector);
  let disposed = false;
  return Object.freeze({
    inspector,
    dispose(): void {
      if (disposed) return;
      disposed = true;
      stopOverlay?.();
      stopEndpoint();
    },
  });
}

/**
 * Streams the same bounded, sanitized events to the loopback development
 * coordinator. The endpoint is injected only by `dotnet webuitoolkit dev`.
 */
export function reportMvvmInspectorToEndpoint(
  inspector: MvvmDevelopmentInspector,
  endpoint: string | URL,
  options: Readonly<MvvmInspectorEndpointReporterOptions> = {},
): () => void {
  const send = options.fetch ?? globalThis.fetch;
  if (typeof send !== "function") {
    throw new Error("The inspector endpoint reporter requires fetch.");
  }
  const target = endpoint.toString();
  return inspector.subscribe(event => {
    void send(target, {
      method: "POST",
      headers: { "content-type": "text/plain;charset=UTF-8" },
      body: serializeInspectorEvent(event),
      keepalive: true,
    }).catch(() => {
      // Development diagnostics must never interrupt application traffic.
    });
  });
}

function serializeInspectorEvent(event: Readonly<MvvmDevelopmentEvent>): string {
  return JSON.stringify(event, (_, value: unknown) =>
    typeof value === "bigint" ? value.toString(10) : value);
}

async function copySourceLocation(
  source: Readonly<MvvmDevelopmentSourceLocation>,
): Promise<void> {
  const value = `${source.file}:${source.line}:${source.column}`;
  if (globalThis.navigator?.clipboard === undefined) return;
  await globalThis.navigator.clipboard.writeText(value);
}

function validateSourceLocation(
  source: Readonly<MvvmDevelopmentSourceLocation>,
): void {
  if (typeof source.file !== "string" ||
      source.file.length === 0 ||
      source.file.length > 1_024 ||
      !Number.isSafeInteger(source.line) ||
      source.line < 1 ||
      !Number.isSafeInteger(source.column) ||
      source.column < 1) {
    throw new TypeError("Inspector source locations require a bounded file and positive line/column.");
  }
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
    ...(correlation?.source === undefined
      ? {}
      : { source: correlation.source }),
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
  readonly source?: Readonly<MvvmDevelopmentSourceLocation>;
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
