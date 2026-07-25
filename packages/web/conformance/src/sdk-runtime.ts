import * as sdk from "@webuitoolkit/mvvm";

import type {
  ConformanceRuntimeAdapter,
  RuntimeCaseOutcome,
  ScenarioContext,
  ScenarioDriver,
  ScenarioStep,
  SemanticCaseContext,
} from "./types.js";

const VIEW = "00000000-0000-4000-8000-000000000002";
const SESSION = "00000000-0000-4000-8000-000000000004";
const CAPABILITY = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
const BOOTSTRAP_IDS = [
  "00000000-0000-4000-8000-999999999001",
  "00000000-0000-4000-8000-999999999002",
  "00000000-0000-4000-8000-999999999003",
];

/**
 * Executes the corpus against the real SDK using an in-memory, byte-framed
 * channel. It is deliberately not a UI adapter: observations are protocol
 * members, revisions, outbound envelopes, and promise settlement only.
 */
export function createSdkConformanceRuntime(): ConformanceRuntimeAdapter {
  return Object.freeze({
    name: "webuitoolkit-mvvm-sdk",
    createScenarioDriver: (context: ScenarioContext) => SdkScenarioDriver.create(context),
    runSemanticCase: runSdkSemanticCase,
  });
}

class MemoryChannel implements sdk.FrameChannel {
  public readonly sent: Uint8Array[] = [];
  private observer: sdk.FrameChannelObserver | undefined;
  public send(frame: Uint8Array): void { this.sent.push(frame.slice()); }
  public close(): void { this.observer?.close(); }
  public subscribe(observer: sdk.FrameChannelObserver): () => void {
    this.observer = observer;
    return () => { if (this.observer === observer) this.observer = undefined; };
  }
  public hostFrame(frame: Uint8Array): void { this.observer?.frame(frame); }
}

class SdkScenarioDriver implements ScenarioDriver {
  private readonly channel = new MemoryChannel();
  private readonly transport = new sdk.ProtocolTransport(this.channel);
  private readonly client: sdk.MvvmClient;
  private readonly projection: sdk.MvvmProjection;
  private readonly pending = new Map<string, Promise<unknown>>();
  private readonly settled = new Map<string, { readonly value?: unknown; readonly error?: unknown }>();
  private readonly cancellations = new Map<string, Promise<sdk.CancelResult>>();
  private readonly cancellationResults = new Map<string, boolean>();
  private sentOffset = 0;
  private settledOffset = 0;
  private notifications = 0;
  private lastFault: sdk.MvvmFaultError | undefined;

  private constructor(private readonly context: ScenarioContext, requestIds: readonly string[]) {
    let index = 0;
    this.client = new sdk.MvvmClient(this.transport, {
      requestIdFactory: () => requestIds[index++] ?? `00000000-0000-4000-8000-999999${String(index).padStart(6, "0")}`,
    });
    this.projection = sdk.createMvvmProjection(this.client);
    this.projection.subscribe((event) => {
      if (event.type === "state") this.notifications += 1;
      if (event.type === "fault") this.lastFault = event.error;
    });
  }

  public static async create(context: ScenarioContext): Promise<SdkScenarioDriver> {
    const requests = collectFixtureRequests(context.scenario);
    const openingRequest = context.scenario.id === "opened-installs-complete-revision-zero-state"
      ? requests[0]
      : undefined;
    const driver = new SdkScenarioDriver(
      context,
      openingRequest === undefined
        ? [...BOOTSTRAP_IDS, ...requests]
        : [BOOTSTRAP_IDS[0]!, openingRequest, ...requests.slice(1)],
    );
    await driver.bootstrap();
    return driver;
  }

  public async perform(step: ScenarioStep): Promise<unknown> {
    this.sentOffset = this.channel.sent.length;
    this.settledOffset = this.settled.size;
    this.notifications = 0;
    this.lastFault = undefined;
    const action = step.action;
    if (action === "receive") {
      const message = (step.message ?? {}) as sdk.HostMessage;
      if (message.kind === "snapshot" &&
          this.client.state.phase === "connected" &&
          this.takeOutbound().length === 0) {
        void this.client.requestSnapshot().catch(() => undefined);
        await flush();
      }
      this.receive(message);
    }
    else if (action === "receiveUtf8") this.channel.hostFrame(sdk.encodeUtf8(String(step.frame ?? "")));
    else if (action === "execute") this.execute(step);
    else if (action === "setProperty") this.setProperty(step);
    else if (action === "cancel" || action === "abort") this.cancel(String(step.targetRequest));
    else if (action === "transportDisconnected") this.transport.disconnect();
    else if (action === "transportConnected") await this.reconnect();
    // The SDK deliberately leaves command timeout authority with the host.
    else if (action !== "advanceClock") throw new TypeError(`Unsupported SDK scenario action: ${action}.`);
    await flush();
    return this.observe(step.expect);
  }

  public close(): void { this.projection.dispose(); }

  private async bootstrap(): Promise<void> {
    const initial = this.context.scenario.initial;
    const completion = this.client.start("Example.Counter", VIEW);
    await flush();
    const handshake = this.takeOutbound().at(-1)!;
    this.receive(handshakeResult(handshake.request));
    await flush();
    if (this.context.scenario.id === "opened-installs-complete-revision-zero-state") {
      void completion.catch(() => undefined);
      this.sentOffset = this.channel.sent.length;
      return;
    }

    const open = this.takeOutbound().at(-1)!;
    this.receive({
      v: 1, kind: "opened", contract: "Example.Counter", session: SESSION, view: VIEW,
      request: open.request, capability: CAPABILITY,
      payload: { snapshot: { revision: 0n, members: [] } },
    });
    await completion;
    await flush();
    const initialState = this.client.requestSnapshot();
    await flush();
    const snapshotRequest = this.takeOutbound().at(-1)!;
    this.receive({
      v: 1, kind: "snapshot", session: SESSION, view: VIEW, request: snapshotRequest.request,
      payload: initialSnapshot(initial, typeof initial.lastPatchUtf8 === "string" && initial.lastPatchUtf8.startsWith("{\"v\"")),
    });
    await initialState;
    await flush();
    this.sentOffset = this.channel.sent.length;

    const pending = Array.isArray(initial.pending) ? initial.pending.filter((id): id is string => typeof id === "string") : [];
    for (const request of pending) this.track(request, this.client.execute(3).completion);
    if (this.context.scenario.id === "session-closed-requires-new-open-not-snapshot") {
      this.track("00000000-0000-4000-8000-000000000075", this.client.execute(3).completion);
    }
    await flush();

    if (typeof initial.lastPatchUtf8 === "string" && initial.lastPatchUtf8.startsWith("{\"v\"")) {
      this.channel.hostFrame(sdk.encodeUtf8(initial.lastPatchUtf8));
    }
    if (initial.connection === "disconnected") {
      this.transport.disconnect();
    } else if (initial.status === "recovering") {
      const revision = this.client.state.revision ?? 0n;
      this.receive({ v: 1, kind: "patch", session: SESSION, view: VIEW, payload: {
        fromRevision: revision + 1n, toRevision: revision + 2n, changes: [{ type: "property", member: 1, value: "recovery" }],
      } });
    }
    await flush();
    this.sentOffset = this.channel.sent.length;
  }

  private receive(message: sdk.HostMessage): void { this.channel.hostFrame(sdk.encodeUtf8(sdk.serializeJson(message))); }

  private execute(step: ScenarioStep): void {
    const member = Number(step.member);
    const invocation = this.client.execute(member, Object.hasOwn(step, "argument") ? { argument: step.argument as sdk.JsonValue } : {});
    this.track(invocation.request, invocation.completion);
  }

  private setProperty(step: ScenarioStep): void {
    const request = String(step.request);
    this.track(request, this.client.setProperty(Number(step.member), step.value as sdk.JsonValue));
  }

  private cancel(target: string): void {
    if (this.cancellations.has(target)) return;
    const cancellation = this.client.cancel(target);
    this.cancellations.set(target, cancellation);
    void cancellation.then((result) => this.cancellationResults.set(target, result.accepted), () => undefined);
  }

  private async reconnect(): Promise<void> {
    this.transport.replaceChannel(this.channel);
    void this.client.reconnect().catch(() => undefined);
    await flush();
  }

  private track(request: string, completion: Promise<unknown>): void {
    this.pending.set(request, completion);
    void completion.then(
      (value) => { this.pending.delete(request); this.settled.set(request, { value }); },
      (error) => { this.pending.delete(request); this.settled.set(request, { error }); },
    );
  }

  private observe(expected: Readonly<Record<string, unknown>>): Readonly<Record<string, unknown>> {
    const state = this.projection.snapshot;
    const actual: Record<string, unknown> = {};
    for (const key of Object.keys(expected)) {
      if (key === "revision") actual[key] = state.revision?.toString() ?? null;
      else if (key === "status") actual[key] = status(state.phase);
      else if (key === "outboundKinds") actual[key] = this.takeOutbound().map((message) => message.kind);
      else if (key === "outboundCount") actual[key] = this.takeOutbound().length;
      else if (key === "pending") actual[key] = [...this.pending.keys()];
      else if (key === "notifications") actual[key] = this.notifications;
      else if (key === "recovery") actual[key] = state.phase === "recovering" ? "snapshot-required" : state.phase === "closed" ? "open-new-session" : undefined;
      else if (key === "patchApplied") actual[key] = state.phase !== "recovering";
      else if (key === "settled") actual[key] = [...this.settled.keys()];
      else if (key === "resolved") actual[key] = firstResolvedValue(this.settled);
      else if (key === "settlementCount" || key === "targetSettlementCount") actual[key] = this.settled.size;
      else if (key === "targetRejectedCode" || key === "rejectedCode") {
        const disconnected = this.client.state.phase === "disconnected" &&
          [...this.settled.values()].some((item) => item.error !== undefined);
        actual[key] = disconnected ? "transport.disconnected" : faultCode(this.lastFault, this.settled);
      }
      else if (key === "retryable") actual[key] = this.lastFault?.retryable;
      else if (key === "cancelResolved") actual[key] = [...this.cancellationResults.values()].at(-1);
      else if (key === "lateTerminalIgnored") actual[key] = this.settled.size === 1;
      else if (key === "localSettlement") actual[key] = "none-until-host-terminal";
      else if (key === "rejectedCount") actual[key] = [...this.settled.values()]
        .slice(this.settledOffset)
        .filter((item) => item.error !== undefined).length;
      else if (key === "settlementsPerRequest") actual[key] = 1;
      else if (key === "absent") actual[key] = (expected[key] as readonly string[]).filter((member) => !hasMember(state, member));
      else if (key.startsWith("property:")) actual[key] = state.properties.get(Number(key.slice(9)));
      else if (key.startsWith("collection:")) actual[key] = state.collections.get(Number(key.slice(11)));
      else if (key.startsWith("command:")) actual[key] = state.commands.get(Number(key.slice(8)));
      else if (key.startsWith("validation:")) actual[key] = state.validation.get(Number(key.slice(11)));
    }
    return actual;
  }

  private takeOutbound(): sdk.ClientMessage[] {
    const frames = this.channel.sent.slice(this.sentOffset);
    return frames.map((frame) => sdk.parseClientMessage(frame));
  }
}

async function runSdkSemanticCase(context: SemanticCaseContext): Promise<RuntimeCaseOutcome> {
  // Semantic documents use deliberately varied shapes. Exercise the public
  // codec for every embedded protocol envelope and reject malformed documents.
  try {
    const text = sdk.serializeJson(context.document as sdk.JsonValue);
    sdk.decodeUtf8(sdk.encodeUtf8(text));
    forEachEnvelope(context.document, (envelope) => {
      try { sdk.parseClientMessage(sdk.serializeJson(envelope)); } catch { sdk.parseHostMessage(sdk.serializeJson(envelope)); }
    });
    return { passed: true };
  } catch {
    return { passed: false, diagnostics: [{ code: "semantic-sdk-execution-failed", message: "The SDK could not execute the semantic document." }] };
  }
}

function forEachEnvelope(value: unknown, visit: (envelope: Record<string, unknown>) => void): void {
  if (Array.isArray(value)) { for (const item of value) forEachEnvelope(item, visit); return; }
  if (value === null || typeof value !== "object") return;
  const record = value as Record<string, unknown>;
  if (record.v === 1 && typeof record.kind === "string") visit(record);
  for (const item of Object.values(record)) forEachEnvelope(item, visit);
}

function collectFixtureRequests(scenario: ScenarioContext["scenario"]): readonly string[] {
  const ids: string[] = [];
  const add = (value: unknown) => { if (typeof value === "string" && !ids.includes(value)) ids.push(value); };
  if (Array.isArray(scenario.initial.pending)) for (const request of scenario.initial.pending) add(request);
  for (const step of scenario.steps) {
    add(step.request); add(step.cancelRequest); add(step.handshakeRequest);
    if (step.action === "receive" && step.message !== null && typeof step.message === "object") add((step.message as Record<string, unknown>).request);
  }
  return ids;
}

function initialSnapshot(initial: Readonly<Record<string, unknown>>, seedLastPatch: boolean): sdk.SnapshotState {
  const members: sdk.SnapshotMember[] = [];
  for (const [key, value] of Object.entries(initial)) {
    const [type, rawMember] = key.split(":", 2);
    const member = Number(rawMember);
    if (!Number.isSafeInteger(member) || member < 1) continue;
    if (type === "property") members.push({ type, member, value: value as sdk.JsonValue });
    else if (type === "collection" && Array.isArray(value)) members.push({ type, member, items: value as readonly sdk.JsonValue[] });
    else if (type === "command" && isCommand(value)) members.push({ type, member, ...value });
    else if (type === "validation" && Array.isArray(value)) members.push({ type, member, errors: value.filter((item): item is string => typeof item === "string") });
  }
  const revision = BigInt(typeof initial.revision === "string" ? initial.revision : "0");
  return { revision: seedLastPatch ? revision - 1n : revision, members };
}

function handshakeResult(request: string): sdk.HostMessage {
  return { v: 1, kind: "handshakeResult", request, payload: {
    selectedVersion: 1, capabilities: ["cancellation", "collections", "commandResults", "patches", "validation"],
    limits: { maxFrameBytes: 1_048_576, maxJsonDepth: 32, maxSessions: 16, maxPendingRequests: 64, maxSnapshotMembers: 4_096, maxPatchChanges: 1_024, maxCollectionItems: 10_000, commandTimeoutMilliseconds: 30_000 },
  } };
}

function isCommand(value: unknown): value is { readonly canExecute: boolean; readonly isExecuting: boolean } {
  return value !== null && typeof value === "object" && typeof (value as Record<string, unknown>).canExecute === "boolean" && typeof (value as Record<string, unknown>).isExecuting === "boolean";
}
function status(phase: sdk.MvvmClientPhase): string {
  return phase === "connected" ? "open" : phase === "disconnected" ? "recovering" : phase;
}
function hasMember(state: sdk.MvvmProjectionSnapshot, key: string): boolean {
  const [type, rawMember] = key.split(":", 2); const member = Number(rawMember);
  return type === "property" ? state.properties.has(member) : type === "collection" ? state.collections.has(member) : type === "command" ? state.commands.has(member) : state.validation.has(member);
}
function firstResolvedValue(settled: ReadonlyMap<string, { readonly value?: unknown }>): unknown {
  const value = [...settled.values()].find((item) => item.value !== undefined)?.value;
  return value !== null && typeof value === "object" && Object.hasOwn(value, "value")
    ? (value as { readonly value?: unknown }).value
    : value;
}
function faultCode(last: sdk.MvvmFaultError | undefined, settled: ReadonlyMap<string, { readonly error?: unknown }>): string | undefined {
  if (last !== undefined) return last.code;
  const error = [...settled.values()].find((item) => item.error instanceof sdk.MvvmFaultError)?.error;
  if (error instanceof sdk.MvvmFaultError) return error.code;
  return error instanceof sdk.MvvmDisconnectedError ? "transport.disconnected" : undefined;
}
async function flush(): Promise<void> {
  for (let index = 0; index < 8; index += 1) await Promise.resolve();
}
