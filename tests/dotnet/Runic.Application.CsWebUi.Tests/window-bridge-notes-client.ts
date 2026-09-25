// Fixture-only authoring sketch over the hand-written Notes routes. This is
// deliberately local to the browser test, not a generated or public SDK API.

export type TitleSnapshot = Readonly<{ value: string; version: number }>;

export type TitleReceipt =
  | Readonly<{ ok: true; kind: "applied"; current: TitleSnapshot; error: null }>
  | Readonly<{ ok: false; kind: "conflict" | "expired"; current: TitleSnapshot; error: null }>
  | Readonly<{ ok: false; kind: "committed-with-error"; current: TitleSnapshot; error: string }>
  | Readonly<{ ok: false; kind: "rejected"; current: TitleSnapshot | null; error: string }>;

type SaveKind = "unknown" | "expired" | "running" | "succeeded" | "rejected" | "cancelled" | "failed";
type AcceptedSaveKind = Exclude<SaveKind, "unknown" | "expired">;
type ObservedSaveKind = Exclude<SaveKind, "unknown">;

export type SaveAdmission =
  | Readonly<{ accepted: true; requestId: string; kind: AcceptedSaveKind }>
  | Readonly<{ accepted: false; kind: ObservedSaveKind; requestId?: string }>;

export type SaveStatus = Readonly<{
  requestId?: string;
  kind: SaveKind;
  state: unknown | null;
  error?: unknown | null;
}>;

export class SaveUncertainError extends Error {
  readonly requestId: string;
  readonly phase: "admission" | "completion";

  constructor(requestId: string, phase: "admission" | "completion", cause: unknown) {
    super(`The Save ${phase} reply was lost or invalid; reconcile request ${requestId} before retrying.`, { cause });
    this.name = "SaveUncertainError";
    this.requestId = requestId;
    this.phase = phase;
  }
}

export interface EditorLease {
  readonly presentationId: string;
  mount(): Promise<void>;
  title(): Promise<TitleSnapshot>;
  setTitle(value: string, requestId?: string): Promise<TitleReceipt>;
  writeTitle(value: string, expected: TitleSnapshot, requestId?: string): Promise<TitleReceipt>;
  save(requestId?: string): Promise<void>;
  startSave(requestId?: string): Promise<SaveAdmission>;
  release(): Promise<void>;
}

export interface NotesClient {
  editor(): EditorLease;
  saveStatus(requestId: string): Promise<SaveStatus>;
  navigateToPreview(): Promise<void>;
}

type Endpoint = Readonly<{ endpoint: string; generation: number }>;
type Manifest = Readonly<{ revision: number; endpoints: Record<string, Endpoint> }>;
type BridgeBootstrap = Readonly<{
  credential: string;
  documentEpoch: string;
  endpoints: Record<string, Endpoint>;
}>;

declare global {
  var runicCsWebUi: BridgeBootstrap;
  var webui: { call(name: string, ...arguments_: string[]): Promise<string> };
  var __runicBridgeEndpointHandoff: (handoff: { v: 1 } & Manifest) => void;
}

type Rejected = Readonly<{ ok: false; kind: "rejected" }>;
type Acknowledged = Readonly<{ ok: true }> | Rejected;
type TitleReply = Readonly<{ ok: true; snapshot: TitleSnapshot; error: null }>
  | Readonly<{ ok: false; kind: "rejected"; current: null; error: string }>;
type Routes = {
  __runicBridgeDocumentBegin: { request: {}; reply: Readonly<{ ok: true; kind: "accepted"; manifest: Manifest }> | Rejected };
  "notes.editor.mount": { request: { presentationId: string }; reply: Acknowledged };
  "notes.editor.unmount": { request: { presentationId: string }; reply: Acknowledged };
  "notes.title.get": { request: { presentationId: string }; reply: TitleReply };
  "notes.title.set": { request: { presentationId: string; requestId: string; value: string }; reply: TitleReceipt };
  "notes.title.writeChecked": { request: { presentationId: string; requestId: string; value: string; expected: TitleSnapshot }; reply: TitleReceipt };
  "notes.save.start": { request: { presentationId: string; requestId: string }; reply: SaveAdmission };
  "notes.save.status": { request: { requestId: string }; reply: SaveStatus };
  "notes.save.wait": { request: { requestId: string }; reply: SaveStatus };
  "notes.navigate.preview": { request: {}; reply: Acknowledged };
};

function wireObject(value: unknown, route: string): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    throw new Error(`Invalid ${route} reply: expected an object.`);
  return value as Record<string, unknown>;
}

function wireKeys(value: Record<string, unknown>, route: string, required: readonly string[], optional: readonly string[] = []): void {
  if (required.some(key => !Object.hasOwn(value, key))
      || Object.keys(value).some(key => !required.includes(key) && !optional.includes(key)))
    throw new Error(`Invalid ${route} reply: unexpected fields.`);
}

function wireString(value: unknown, route: string, field: string): string {
  if (typeof value !== "string") throw new Error(`Invalid ${route} reply: ${field} must be a string.`);
  return value;
}

function wireInteger(value: unknown, route: string, field: string, minimum: number): number {
  if (!Number.isSafeInteger(value) || (value as number) < minimum)
    throw new Error(`Invalid ${route} reply: ${field} must be a safe integer.`);
  return value as number;
}

function wireSnapshot(value: unknown, route: string): TitleSnapshot {
  const snapshot = wireObject(value, route);
  wireKeys(snapshot, route, ["value", "version"]);
  return { value: wireString(snapshot.value, route, "value"), version: wireInteger(snapshot.version, route, "version", 0) };
}

function wireManifest(value: unknown, route: string): Manifest {
  const manifest = wireObject(value, route);
  wireKeys(manifest, route, ["revision", "endpoints"]);
  const endpoints = wireObject(manifest.endpoints, route);
  return {
    revision: wireInteger(manifest.revision, route, "revision", 0),
    endpoints: Object.fromEntries(Object.entries(endpoints).map(([name, raw]) => {
      const endpoint = wireObject(raw, route);
      wireKeys(endpoint, route, ["endpoint", "generation"]);
      const id = wireString(endpoint.endpoint, route, `${name}.endpoint`);
      if (!/^[0-9A-F]{32}$/.test(id)) throw new Error(`Invalid ${route} reply: ${name}.endpoint is malformed.`);
      return [name, { endpoint: id, generation: wireInteger(endpoint.generation, route, `${name}.generation`, 1) }];
    })),
  };
}

function wireAcknowledged(value: unknown, route: string): Acknowledged {
  const reply = wireObject(value, route);
  if (reply.ok === true) { wireKeys(reply, route, ["ok"]); return { ok: true }; }
  if (reply.ok === false && reply.kind === "rejected") {
    wireKeys(reply, route, ["ok", "kind"]);
    return { ok: false, kind: "rejected" };
  }
  throw new Error(`Invalid ${route} reply: unknown acknowledgment.`);
}

function wireBegin(value: unknown): Routes["__runicBridgeDocumentBegin"]["reply"] {
  const route = "__runicBridgeDocumentBegin";
  const reply = wireObject(value, route);
  if (reply.ok === true && reply.kind === "accepted") {
    wireKeys(reply, route, ["ok", "kind", "manifest"]);
    return { ok: true, kind: "accepted", manifest: wireManifest(reply.manifest, route) };
  }
  if (reply.ok === false && ["rejected", "unissued-document", "stale-document"].includes(String(reply.kind))) {
    wireKeys(reply, route, ["ok", "kind"], ["error"]);
    if (reply.error !== undefined) wireString(reply.error, route, "error");
    return { ok: false, kind: "rejected" };
  }
  throw new Error(`Invalid ${route} reply: unknown admission.`);
}

function wireTitleReply(value: unknown): TitleReply {
  const route = "notes.title.get";
  const reply = wireObject(value, route);
  if (reply.ok === true) {
    wireKeys(reply, route, ["ok", "snapshot", "error"]);
    if (reply.error !== null) throw new Error(`Invalid ${route} reply: success carried an error.`);
    return { ok: true, snapshot: wireSnapshot(reply.snapshot, route), error: null };
  }
  if (reply.ok === false && reply.kind === "rejected") {
    wireKeys(reply, route, ["ok", "kind", "current", "error"]);
    if (reply.current !== null) throw new Error(`Invalid ${route} reply: rejection carried a snapshot.`);
    return { ok: false, kind: "rejected", current: null, error: wireString(reply.error, route, "error") };
  }
  throw new Error(`Invalid ${route} reply: unknown title result.`);
}

function wireTitleReceipt(value: unknown, route: string): TitleReceipt {
  const reply = wireObject(value, route);
  wireKeys(reply, route, ["ok", "kind", "current", "error"]);
  if (reply.ok === true && reply.kind === "applied" && reply.error === null)
    return { ok: true, kind: "applied", current: wireSnapshot(reply.current, route), error: null };
  if (reply.ok === false) {
    if ((reply.kind === "conflict" || reply.kind === "expired") && reply.error === null)
      return { ok: false, kind: reply.kind, current: wireSnapshot(reply.current, route), error: null };
    if (reply.kind === "committed-with-error")
      return { ok: false, kind: reply.kind, current: wireSnapshot(reply.current, route), error: wireString(reply.error, route, "error") };
    if (reply.kind === "rejected")
      return { ok: false, kind: reply.kind,
        current: reply.current === null ? null : wireSnapshot(reply.current, route),
        error: wireString(reply.error, route, "error") };
  }
  throw new Error(`Invalid ${route} reply: unknown title receipt.`);
}

function wireSaveAdmission(value: unknown): SaveAdmission {
  const route = "notes.save.start";
  const reply = wireObject(value, route);
  if (reply.accepted === true && acceptedSaveKinds.includes(reply.kind as AcceptedSaveKind)) {
    wireKeys(reply, route, ["accepted", "requestId", "kind"]);
    return { accepted: true, requestId: wireString(reply.requestId, route, "requestId"), kind: reply.kind as AcceptedSaveKind };
  }
  if (reply.accepted === false && observedSaveKinds.includes(reply.kind as ObservedSaveKind)) {
    wireKeys(reply, route, ["accepted", "kind"], ["requestId"]);
    return reply.requestId === undefined
      ? { accepted: false, kind: reply.kind as ObservedSaveKind }
      : { accepted: false, kind: reply.kind as ObservedSaveKind, requestId: wireString(reply.requestId, route, "requestId") };
  }
  throw new Error(`Invalid ${route} reply: unknown save admission.`);
}

const saveKinds: readonly SaveKind[] = ["unknown", "expired", "running", "succeeded", "rejected", "cancelled", "failed"];
const acceptedSaveKinds: readonly AcceptedSaveKind[] = ["running", "succeeded", "rejected", "cancelled", "failed"];
const observedSaveKinds: readonly ObservedSaveKind[] = ["expired", ...acceptedSaveKinds];

function wireSaveStatus(value: unknown, route: string): SaveStatus {
  const reply = wireObject(value, route);
  wireKeys(reply, route, ["kind", "state"], ["requestId", "error"]);
  if (!saveKinds.includes(reply.kind as SaveKind))
    throw new Error(`Invalid ${route} reply: unknown save status.`);
  if (reply.kind === "rejected" && reply.state !== null)
    throw new Error(`Invalid ${route} reply: rejected status carried state.`);
  if (reply.kind !== "rejected" && (reply.requestId === undefined || reply.error === undefined))
    throw new Error(`Invalid ${route} reply: operation status is incomplete.`);
  if (reply.state !== null && typeof reply.state !== "string")
    throw new Error(`Invalid ${route} reply: state must be a string or null.`);
  if (reply.error !== undefined && reply.error !== null) wireString(reply.error, route, "error");
  const result: SaveStatus = { kind: reply.kind as SaveStatus["kind"], state: reply.state,
    ...(reply.requestId === undefined ? {} : { requestId: wireString(reply.requestId, route, "requestId") }),
    ...(reply.error === undefined ? {} : { error: reply.error }) };
  return result;
}

const replyDecoders: { [Route in keyof Routes]: (value: unknown) => Routes[Route]["reply"] } = {
  __runicBridgeDocumentBegin: wireBegin,
  "notes.editor.mount": value => wireAcknowledged(value, "notes.editor.mount"),
  "notes.editor.unmount": value => wireAcknowledged(value, "notes.editor.unmount"),
  "notes.title.get": wireTitleReply,
  "notes.title.set": value => wireTitleReceipt(value, "notes.title.set"),
  "notes.title.writeChecked": value => wireTitleReceipt(value, "notes.title.writeChecked"),
  "notes.save.start": wireSaveAdmission,
  "notes.save.status": value => wireSaveStatus(value, "notes.save.status"),
  "notes.save.wait": value => wireSaveStatus(value, "notes.save.wait"),
  "notes.navigate.preview": value => wireAcknowledged(value, "notes.navigate.preview"),
};

function requireAccepted(result: Acknowledged, action: string): void {
  if (!result.ok) throw new Error(`${action} was rejected.`);
}

export async function connectNotes(): Promise<NotesClient> {
  const bridge = globalThis.runicCsWebUi;
  const webui = globalThis.webui;
  const documentEpoch = bridge.documentEpoch;

  async function call<Route extends keyof Routes>(
    route: Route, request: Routes[Route]["request"],
  ): Promise<Routes[Route]["reply"]> {
    // Dynamic routes can arrive after HTML load. Resolve the current descriptor
    // for every call, while binding this client to its original document epoch.
    let descriptor = bridge.endpoints[route];
    if (!descriptor && route !== "__runicBridgeDocumentBegin") {
      // A one-way dynamic handoff may have been missed. Reconcile through the
      // host-owned fixed begin route before sending this operation; never
      // replay a call whose reply was lost after dispatch.
      const current = await call("__runicBridgeDocumentBegin", {});
      if (!current.ok) throw new Error("The Notes document is no longer admitted.");
      globalThis.__runicBridgeEndpointHandoff({ v: 1, ...current.manifest });
      descriptor = bridge.endpoints[route];
    }
    if (!descriptor) throw new Error(`Notes route ${route} is unavailable.`);
    const raw = await webui.call("__runicBridgeDispatch", bridge.credential,
      JSON.stringify({
        v: 1,
        endpoint: descriptor.endpoint,
        generation: descriptor.generation,
        payload: { ...request, documentEpoch },
      }));
    let parsed: unknown;
    try { parsed = JSON.parse(raw); }
    catch { throw new Error(`Invalid ${route} reply: malformed JSON.`); }
    return replyDecoders[route](parsed);
  }

  const admission = await call("__runicBridgeDocumentBegin", {});
  if (!admission.ok) throw new Error("Notes document was not admitted.");
  globalThis.__runicBridgeEndpointHandoff({ v: 1, ...admission.manifest });

  return {
    editor(): EditorLease {
      const presentationId = `editor:${crypto.randomUUID()}`;
      let mounted = false;
      let released = false;
      let mountPending: Promise<void> | undefined;
      let releasePending: Promise<void> | undefined;

      function requireMounted(): void {
        if (released || !mounted) throw new Error("The Editor owner is not mounted.");
      }

      return {
        presentationId,
        mount(): Promise<void> {
          if (released) throw new Error("A released Editor owner cannot remount.");
          if (mounted) return Promise.resolve();
          mountPending ??= (async () => {
            try {
              requireAccepted(await call("notes.editor.mount", { presentationId }), "Editor mount");
              mounted = true;
            } finally {
              if (!mounted) mountPending = undefined;
            }
          })();
          return mountPending;
        },
        async title(): Promise<TitleSnapshot> {
          requireMounted();
          const reply = await call("notes.title.get", { presentationId });
          if (!reply.ok) throw new Error("Title snapshot was rejected.");
          return reply.snapshot;
        },
        async setTitle(value: string, requestId = crypto.randomUUID()): Promise<TitleReceipt> {
          requireMounted();
          return call("notes.title.set", { presentationId, requestId, value });
        },
        async writeTitle(value: string, expected: TitleSnapshot, requestId = crypto.randomUUID()): Promise<TitleReceipt> {
          requireMounted();
          return call("notes.title.writeChecked", { presentationId, requestId, value, expected });
        },
        async startSave(requestId = crypto.randomUUID()): Promise<SaveAdmission> {
          requireMounted();
          return call("notes.save.start", { presentationId, requestId });
        },
        async save(requestId = crypto.randomUUID()): Promise<void> {
          requireMounted();
          let admission: SaveAdmission;
          try { admission = await call("notes.save.start", { presentationId, requestId }); }
          catch (cause) { throw new SaveUncertainError(requestId, "admission", cause); }
          if (admission.requestId !== undefined && admission.requestId !== requestId)
            throw new SaveUncertainError(requestId, "admission", new Error("The host returned another request ID."));
          if (!admission.accepted && ["rejected", "expired", "unknown"].includes(admission.kind))
            throw new Error(`Save ${admission.kind} for request ${requestId}; reconcile before retrying.`);
          let terminal: SaveStatus;
          try { terminal = await call("notes.save.wait", { requestId }); }
          catch (cause) { throw new SaveUncertainError(requestId, "completion", cause); }
          if (terminal.requestId !== requestId)
            throw new SaveUncertainError(requestId, "completion", new Error("The host returned another request ID."));
          if (terminal.kind !== "succeeded")
            throw new Error(`Save ${terminal.kind} for request ${requestId}: ${String(terminal.error ?? "no further detail")}`);
        },
        release(): Promise<void> {
          if (releasePending) return releasePending;
          released = true;
          releasePending = (async () => {
            // Framework teardown may outrun its own pending mount callback.
            if (mountPending) {
              try { await mountPending; } catch { return; }
            }
            if (!mounted) return;
            mounted = false;
            // Window navigation can retire the server presentation before the
            // frontend framework disposes its component. The owner is already
            // locally released, so a rejected unmount is a completed teardown.
            await call("notes.editor.unmount", { presentationId });
          })();
          return releasePending;
        },
      };
    },
    saveStatus(requestId: string): Promise<SaveStatus> {
      return call("notes.save.status", { requestId });
    },
    async navigateToPreview(): Promise<void> {
      requireAccepted(await call("notes.navigate.preview", {}), "Preview navigation");
    },
  };
}
