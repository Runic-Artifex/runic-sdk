import {
  Effect,
  Exit,
  Fiber,
  Layer,
  ManagedRuntime,
  PubSub,
  Runtime,
  Schema,
  Stream,
} from "effect";
import {
  ClientEnvelopeSchema,
  HostEnvelopeSchema,
  type ApplicationContract,
  type ClientEnvelope,
  type HostEnvelope,
} from "./contract.js";
import { bridgeError, type BridgeError } from "./errors.js";
import { ApplicationBridge, type ApplicationBridgeService } from "./service.js";
import type { FrameChannel, FrameChannelEvent, ReconnectableFrameChannel } from "./transport.js";

export interface ApplicationBridgeOptions {
  /** Produces a candidate command identifier. Intended primarily for deterministic tests. */
  readonly commandIdFactory?: () => string;
  readonly maxFrameBytes?: number;
  readonly maxPendingCommands?: number;
  readonly maxBufferedFrames?: number;
  readonly maxBufferedEvents?: number;
  readonly maxBatchFrames?: number;
  /** Receives sanitized bridge connection and contract diagnostics. */
  readonly onDiagnostic?: (diagnostic: ApplicationBridgeDiagnostic) => void;
}

export interface ApplicationBridgeDiagnostic {
  readonly code: "contract-mismatch" | "connection-lost" | "resynchronization-required";
  readonly message: string;
  readonly connectionEpoch: number;
  /** Present when outstanding requests were terminally discarded. */
  readonly pendingCommandDisposition?: PendingCommandDisposition;
}

/** Effect programs backed by the controller's single owned ManagedRuntime. */
export interface ApplicationBridgeEffects<Command, Receipt, HostEvent, Snapshot> {
  readonly initialize: Effect.Effect<Snapshot, BridgeError, ApplicationBridgeService>;
  readonly dispatch: (
    command: Command,
  ) => Effect.Effect<Receipt, BridgeError, ApplicationBridgeService>;
  readonly cancel: (
    operationId: string,
  ) => Effect.Effect<void, BridgeError, ApplicationBridgeService>;
  readonly reconnect: Effect.Effect<Snapshot, BridgeError, ApplicationBridgeService>;
  readonly uiReady: Effect.Effect<void, BridgeError, ApplicationBridgeService>;
  readonly uiRendered: Effect.Effect<void, BridgeError, ApplicationBridgeService>;
  readonly events: Stream.Stream<HostEvent, BridgeError, ApplicationBridgeService>;
}

/**
 * Promise convenience methods plus an opt-in Effect surface. All programs run
 * in the same ManagedRuntime; adapters must not construct a renderer runtime.
 */
export interface ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot> {
  readonly effects: ApplicationBridgeEffects<Command, Receipt, HostEvent, Snapshot>;
  initialize(): Promise<Snapshot>;
  dispatch(command: Command): Promise<Receipt>;
  cancel(operationId: string): Promise<void>;
  reconnect(): Promise<Snapshot>;
  uiReady(): Promise<void>;
  uiRendered(): Promise<void>;
  subscribe(
    onEvent: (event: HostEvent) => void,
    onError?: (error: BridgeError) => void,
  ): () => void;
  run<A, E>(program: Effect.Effect<A, E, ApplicationBridgeService>): Promise<A>;
  runExit<A, E>(
    program: Effect.Effect<A, E, ApplicationBridgeService>,
  ): Promise<Exit.Exit<A, E>>;
  fork<A, E>(
    program: Effect.Effect<A, E, ApplicationBridgeService>,
  ): Fiber.RuntimeFiber<A, E>;
  await<A, E>(fiber: Fiber.RuntimeFiber<A, E>): Promise<Exit.Exit<A, E>>;
  interrupt<A, E>(fiber: Fiber.RuntimeFiber<A, E>): Promise<Exit.Exit<A, E>>;
  dispose(): Promise<void>;
}

interface Pending {
  readonly kind: "initialize" | "dispatch" | "cancel" | "uiReady" | "uiRendered";
  readonly resolve: (value: unknown) => void;
  readonly reject: (error: BridgeError) => void;
  readonly connectionEpoch: number;
}

/** Terminal disposition for requests that cannot survive a physical reconnect. */
export type PendingCommandDisposition = "transport-lost" | "resynchronization-required";

interface BufferedFrame {
  readonly generation: number;
  readonly event: FrameChannelEvent;
}

const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });
const UiReadyReceiptSchema = Schema.TaggedStruct("UiReadyAccepted", {});
const UiRenderedReceiptSchema = Schema.TaggedStruct("UiRenderedAccepted", {});

/** Transport-neutral Application Bridge layer over any structural FrameChannel. */
export function ApplicationBridgeLive<Command, Receipt, HostEvent, Snapshot>(
  contract: ApplicationContract<Command, Receipt, HostEvent, Snapshot>,
  channel: FrameChannel,
  options: ApplicationBridgeOptions = {},
): Layer.Layer<ApplicationBridgeService> {
  return Layer.scoped(
    ApplicationBridge,
    Effect.gen(function*() {
      const maxFrameBytes = positiveInteger(options.maxFrameBytes ?? 262_144, "maxFrameBytes");
      const maxPending = positiveInteger(options.maxPendingCommands ?? 64, "maxPendingCommands");
      const maxBufferedFrames = positiveInteger(options.maxBufferedFrames ?? 256, "maxBufferedFrames");
      const maxBufferedEvents = positiveInteger(options.maxBufferedEvents ?? 1_024, "maxBufferedEvents");
      const maxBatchFrames = positiveInteger(options.maxBatchFrames ?? 4_096, "maxBatchFrames");
      const events = yield* PubSub.dropping<HostEvent>(maxBufferedEvents);
      const failures = yield* PubSub.unbounded<BridgeError>();
      const rawFrames = yield* PubSub.dropping<BufferedFrame>(maxBufferedFrames);
      const pending = new Map<string, Pending>();
      const nextCommandId = options.commandIdFactory ?? (() => crypto.randomUUID());
      let sessionId: string | undefined;
      let revision = 0;
      let sequence = 0;
      let stopped = false;
      let recoveryRequired = false;
      let ingressGeneration = 0;
      let connectionEpoch = 0;
      let reconnectPromise: Promise<Snapshot> | undefined;
      const effectRuntime = yield* Effect.runtime<never>();
      const runPromise = Runtime.runPromise(effectRuntime);
      const runSync = Runtime.runSync(effectRuntime);

      const disposePending = (error: BridgeError): void => {
        for (const item of pending.values()) item.reject(error);
        pending.clear();
      };
      const failAll = (error: BridgeError): Effect.Effect<void> => Effect.sync(() => disposePending(error)).pipe(
        Effect.zipRight(PubSub.publish(failures, error)),
        Effect.asVoid,
      );

      const requireRecovery = (error: BridgeError): Effect.Effect<void> => Effect.sync(() => {
        if (!recoveryRequired) ingressGeneration++;
        recoveryRequired = true;
        sessionId = undefined;
        sequence = 0;
        try {
          options.onDiagnostic?.({
            code: error._tag === "ProtocolVersionMismatch" ? "contract-mismatch"
              : error._tag === "TransportClosed" || error._tag === "TransportUnavailable" ? "connection-lost"
                : "resynchronization-required",
            message: error.message,
            connectionEpoch,
            pendingCommandDisposition: error._tag === "TransportClosed" || error._tag === "TransportUnavailable"
              ? "transport-lost"
              : "resynchronization-required",
          });
        } catch {
          // Diagnostics must not affect protocol recovery or command disposition.
        }
      }).pipe(Effect.zipRight(failAll(error)));

      const onEnvelope = (envelope: HostEnvelope): Effect.Effect<void, BridgeError> => Effect.gen(function*() {
        if (envelope.protocol !== contract.identity || envelope.version !== contract.version ||
          envelope.contractFingerprint !== contract.fingerprint) {
          return yield* Effect.fail(bridgeError("ProtocolVersionMismatch", "The host uses an incompatible Application Bridge contract."));
        }
        // Old frames are an expected consequence of replacing a physical
        // connection. They are terminally detached from the new epoch.
        if (envelope.connectionEpoch < connectionEpoch) return;
        if (envelope.connectionEpoch > connectionEpoch) {
          // Only a pending initialize may legitimately receive a precommit
          // admission refusal for its requested future epoch. Other future
          // sequence-zero errors are hostile or stale and cannot affect work.
          if (envelope.kind === "error" && envelope.sequence === 0) {
            const pendingInitialize = envelope.commandId === undefined ? undefined : pending.get(envelope.commandId);
            if (pendingInitialize?.kind === "initialize" && pendingInitialize.connectionEpoch === envelope.connectionEpoch) {
              pending.delete(envelope.commandId!);
              const error = yield* Schema.decodeUnknown(contract.error!, { onExcessProperty: "error" })(envelope.payload).pipe(
                Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host admission error was invalid.")),
              );
              pendingInitialize.reject(error);
            }
            return;
          }
          return yield* Effect.fail(bridgeError("ProtocolDecodeError", "The host response belongs to a stale connection epoch.", true));
        }
        // Sequence zero is a correlated local-admission refusal. Epoch checks
        // must precede it so delayed old-epoch errors cannot reject a reused id.
        if (envelope.kind === "error" && envelope.sequence === 0) {
          if (envelope.commandId === undefined) {
            return yield* Effect.fail(bridgeError("ProtocolDecodeError", "An admission error omitted its command identifier."));
          }
          const item = pending.get(envelope.commandId);
          if (item === undefined || item.connectionEpoch !== connectionEpoch) return;
          pending.delete(envelope.commandId);
          const error = yield* Schema.decodeUnknown(contract.error!, { onExcessProperty: "error" })(envelope.payload).pipe(
            Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host admission error was invalid.")),
          );
          item.reject(error);
          return;
        }
        if (sessionId !== undefined && envelope.sessionId !== sessionId) {
          return yield* Effect.fail(bridgeError("ProtocolDecodeError", "The host response belongs to a stale session."));
        }
        if (envelope.sequence !== sequence + 1) {
          return yield* Effect.fail(bridgeError("ProtocolDecodeError", "A host event sequence gap requires authoritative recovery.", true));
        }
        sequence = envelope.sequence;
        revision = envelope.revision;
        sessionId = envelope.sessionId;

        if (envelope.kind === "event") {
          const event = yield* Schema.decodeUnknown(contract.event, { onExcessProperty: "error" })(envelope.payload).pipe(
            Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host event was invalid.")),
          );
          const published = yield* PubSub.publish(events, event);
          if (!published) {
            return yield* Effect.fail(bridgeError(
              "ProtocolDecodeError",
              "The validated host event buffer overflowed; authoritative recovery is required.",
              true,
            ));
          }
          return;
        }
        const commandId = envelope.commandId;
        if (commandId === undefined) {
          return yield* Effect.fail(bridgeError("ProtocolDecodeError", "A correlated host response omitted its command identifier."));
        }
        const item = pending.get(commandId);
        if (item === undefined || item.connectionEpoch !== connectionEpoch) return;
        pending.delete(commandId);
        if (envelope.kind === "error") {
          const error = yield* Schema.decodeUnknown(contract.error!, { onExcessProperty: "error" })(envelope.payload).pipe(
            Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host error was invalid.")),
          );
          item.reject(error);
          return;
        }
        if (item.kind === "initialize") {
          if (envelope.kind !== "snapshot") {
            return yield* Effect.fail(bridgeError("ProtocolDecodeError", "The host initialization response was not a snapshot."));
          }
          const snapshot = yield* Schema.decodeUnknown(contract.snapshot, { onExcessProperty: "error" })(envelope.payload).pipe(
            Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host response payload was invalid.")),
          );
          item.resolve(snapshot);
          return;
        }
        if (envelope.kind !== "receipt") {
          return yield* Effect.fail(bridgeError("ProtocolDecodeError", "The host command response was not a receipt."));
        }
        const value: unknown = item.kind === "uiReady"
          ? yield* Schema.decodeUnknown(UiReadyReceiptSchema, { onExcessProperty: "error" })(envelope.payload).pipe(
            Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host UI-ready acknowledgement was invalid.")),
          )
          : item.kind === "uiRendered"
            ? yield* Schema.decodeUnknown(UiRenderedReceiptSchema, { onExcessProperty: "error" })(envelope.payload).pipe(
              Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host UI-rendered acknowledgement was invalid.")),
            )
            : yield* Schema.decodeUnknown(contract.receipt, { onExcessProperty: "error" })(envelope.payload).pipe(
              Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host response payload was invalid.")),
            );
        item.resolve(value);
      });

      const onChannelEvent = (buffered: BufferedFrame): Effect.Effect<void> => Effect.gen(function*() {
        if (buffered.generation !== ingressGeneration || recoveryRequired) return;
        const event = buffered.event;
        if (event._tag === "State") {
          if (event.state !== "connected") {
            yield* requireRecovery(bridgeError(
              event.state === "closed" ? "TransportClosed" : "TransportUnavailable",
              "The Application Bridge transport is unavailable; pending commands were discarded.",
              true,
            ));
          }
          return;
        }
        if (event.bytes.byteLength > maxFrameBytes) {
          return yield* Effect.fail(bridgeError("ProtocolDecodeError", "The host frame exceeded the configured byte limit."));
        }
        let json: unknown;
        try {
          json = JSON.parse(decoder.decode(event.bytes));
        } catch {
          return yield* Effect.fail(bridgeError("ProtocolDecodeError", "The host frame was not valid UTF-8 JSON."));
        }
        const encodedEnvelopes = Array.isArray(json) ? json : [json];
        if (encodedEnvelopes.length === 0 || encodedEnvelopes.length > maxBatchFrames) {
          return yield* Effect.fail(bridgeError(
            "ProtocolDecodeError",
            "The correlated host frame batch exceeded the configured item limit.",
          ));
        }
        for (const encodedEnvelope of encodedEnvelopes) {
          const envelope = yield* Schema.decodeUnknown(HostEnvelopeSchema, { onExcessProperty: "error" })(encodedEnvelope).pipe(
            Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host frame was invalid.")),
          );
          yield* onEnvelope(envelope);
        }
      }).pipe(Effect.catchAll(requireRecovery));

      const unsubscribe = channel.subscribe((event) => {
        const published = runSync(PubSub.publish(rawFrames, { generation: ingressGeneration, event }));
        if (!published && !recoveryRequired) {
          runSync(requireRecovery(bridgeError(
            "ProtocolDecodeError",
            "The host frame ingress buffer overflowed; authoritative recovery is required.",
            true,
          )));
        }
      });
      yield* Stream.fromPubSub(rawFrames).pipe(
        Stream.runForEach(onChannelEvent),
        Effect.forkScoped,
      );
      yield* Effect.addFinalizer(() => Effect.gen(function*() {
        stopped = true;
        unsubscribe();
        yield* failAll(bridgeError("TransportClosed", "The Application Bridge runtime was disposed."));
        yield* PubSub.shutdown(rawFrames);
        yield* PubSub.shutdown(events);
        yield* PubSub.shutdown(failures);
        yield* Effect.promise(() => channel.close("Application Bridge runtime disposed"));
      }));

      const request = <A>(kind: ClientEnvelope["kind"], payload: unknown, expectedRevision?: number): Effect.Effect<A, BridgeError> =>
        Effect.async<A, BridgeError>((resume) => {
          if (stopped) {
            resume(Effect.fail(bridgeError("TransportClosed", "The Application Bridge runtime is disposed.")));
            return;
          }
          if (channel.state !== "connected") {
            resume(Effect.fail(bridgeError("TransportUnavailable", "The Application Bridge transport is unavailable.", true)));
            return;
          }
          if (pending.size >= maxPending) {
            resume(Effect.fail(bridgeError("CommandRejected", "The pending command limit was exceeded.", true)));
            return;
          }
          if (recoveryRequired) {
            resume(Effect.fail(bridgeError(
              "ProtocolDecodeError",
              "The Application Bridge requires authoritative recovery before accepting requests.",
              true,
            )));
            return;
          }
          let commandId = nextCommandId();
          while (pending.has(commandId)) commandId = crypto.randomUUID();
          const item: Pending = {
            kind: kind === "initialize"
              ? "initialize"
              : kind === "dispatch"
                ? "dispatch"
                : kind === "cancelOperation"
                  ? "cancel"
                  : kind,
            resolve: (value) => resume(Effect.succeed(value as A)),
            reject: (error) => resume(Effect.fail(error)),
            connectionEpoch,
          };
          const requestEpoch = connectionEpoch;
          pending.set(commandId, item);
          const envelope: ClientEnvelope = {
            protocol: contract.identity,
            version: contract.version,
            contractFingerprint: contract.fingerprint,
            connectionEpoch,
            kind,
            commandId,
            payload,
            ...(sessionId === undefined ? {} : { sessionId }),
            ...(expectedRevision === undefined ? {} : { expectedRevision }),
          };
          runPromise(Schema.encode(ClientEnvelopeSchema)(envelope))
            .then((encoded) => channel.send(encoder.encode(JSON.stringify(encoded))))
            .catch(() => {
              // A reconnect may have already detached this request. Its late
              // send failure must not tear down the newly admitted epoch.
              if (requestEpoch !== connectionEpoch || pending.get(commandId) !== item) return;
              void runPromise(requireRecovery(bridgeError(
                "TransportUnavailable",
                "The Application Bridge request could not be sent; pending commands were discarded.",
                true,
              )));
            });
        });

      const connect = (): Effect.Effect<void, BridgeError> => {
        if (channel.state === "connected") return Effect.void;
        if (!isReconnectable(channel)) {
          return Effect.fail(bridgeError(
            channel.state === "closed" ? "TransportClosed" : "TransportUnavailable",
            "The Application Bridge transport is unavailable.",
            true,
          ));
        }
        return Effect.async<void, BridgeError>((resume) => {
          let active = true;
          channel.reconnect().then(
            () => {
              if (active) resume(Effect.void);
            },
            () => {
              if (active) {
                resume(Effect.fail(bridgeError(
                  "TransportUnavailable",
                  "The Application Bridge transport could not connect.",
                  true,
                )));
              }
            },
          );
          return Effect.sync(() => {
            active = false;
            void channel.close("Application Bridge connection interrupted");
          });
        });
      };
      const initialize = connect().pipe(
        Effect.zipRight(request<Snapshot>("initialize", contract.initialize)),
      );
      const reconnect: Effect.Effect<Snapshot, BridgeError> = Effect.async((resume) => {
        if (reconnectPromise !== undefined) {
          reconnectPromise.then(
            (snapshot) => resume(Effect.succeed(snapshot)),
            (error: BridgeError) => resume(Effect.fail(error)),
          );
          return;
        }
        const disposed = bridgeError(
          "TransportUnavailable",
          "The Application Bridge reconnect replaced pending commands.",
          true,
        );
        disposePending(disposed);
        ingressGeneration++;
        connectionEpoch++;
        recoveryRequired = false;
        sessionId = undefined;
        sequence = 0;
        revision = 0;
        const current = (async () => {
          if (isReconnectable(channel)) {
            try { await channel.reconnect(); }
            catch {
              throw bridgeError("TransportUnavailable", "The Application Bridge transport could not reconnect.", true);
            }
          }
          if (channel.state !== "connected") {
            throw bridgeError("TransportUnavailable", "The Application Bridge transport could not reconnect.", true);
          }
          return await runPromise(request<Snapshot>("initialize", contract.initialize));
        })();
        reconnectPromise = current;
        current.then(
          (snapshot) => resume(Effect.succeed(snapshot)),
          (error: BridgeError) => resume(Effect.fail(error)),
        ).finally(() => { reconnectPromise = undefined; });
      });
      const service: ApplicationBridgeService<Command, Receipt, HostEvent, Snapshot> = {
        initialize,
        dispatch: (command) => Schema.encode(contract.command)(command).pipe(
          Effect.mapError(() => bridgeError("ProtocolDecodeError", "The command did not satisfy its Effect Schema.")),
          Effect.flatMap((payload) => request<Receipt>("dispatch", payload, revision)),
        ),
        cancel: (operationId) => request<unknown>("cancelOperation", { operationId }, revision).pipe(Effect.asVoid),
        reconnect,
        uiReady: request<unknown>("uiReady", {}).pipe(Effect.asVoid),
        uiRendered: request<unknown>("uiRendered", {}).pipe(Effect.asVoid),
        events: Stream.merge(
          Stream.fromPubSub(events),
          Stream.fromPubSub(failures).pipe(Stream.mapEffect(Effect.fail)),
        ),
      };
      return service as ApplicationBridgeService;
    }),
  );
}

function isReconnectable(channel: FrameChannel): channel is ReconnectableFrameChannel {
  return typeof (channel as Partial<ReconnectableFrameChannel>).reconnect === "function";
}

function positiveInteger(value: number, name: string): number {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new RangeError(`${name} must be a positive safe integer.`);
  }
  return value;
}

export function createApplicationBridgeRuntime(layer: Layer.Layer<ApplicationBridgeService>) {
  const managed = ManagedRuntime.make(layer);
  return {
    runPromise: managed.runPromise.bind(managed),
    runFork: managed.runFork.bind(managed),
    dispose: managed.dispose.bind(managed),
  };
}

/**
 * Framework-neutral imperative edge around the single owned Effect runtime.
 * UI components call this controller; Effect services and fibers remain at the
 * composition boundary.
 */
export function createApplicationBridgeController<Command, Receipt, HostEvent, Snapshot>(
  contract: ApplicationContract<Command, Receipt, HostEvent, Snapshot>,
  layer: Layer.Layer<ApplicationBridgeService>,
): ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot> {
  void contract;
  const runtime = createApplicationBridgeRuntime(layer);
  const service = Effect.map(
    ApplicationBridge,
    (value) => value as ApplicationBridgeService<Command, Receipt, HostEvent, Snapshot>,
  );
  const effects: ApplicationBridgeEffects<Command, Receipt, HostEvent, Snapshot> = {
    initialize: Effect.flatMap(service, (bridge) => bridge.initialize),
    dispatch: (command) => Effect.flatMap(service, (bridge) => bridge.dispatch(command)),
    cancel: (operationId) => Effect.flatMap(service, (bridge) => bridge.cancel(operationId)),
    reconnect: Effect.flatMap(service, (bridge) => bridge.reconnect),
    uiReady: Effect.flatMap(service, (bridge) => bridge.uiReady),
    uiRendered: Effect.flatMap(service, (bridge) => bridge.uiRendered),
    events: Stream.unwrap(Effect.map(service, (bridge) => bridge.events)),
  };
  const run = <A, E>(program: Effect.Effect<A, E, ApplicationBridgeService>) =>
    runtime.runPromise(program);

  return {
    effects,
    initialize: () => run(effects.initialize),
    dispatch: (command: Command) => run(effects.dispatch(command)),
    cancel: (operationId: string) => run(effects.cancel(operationId)),
    reconnect: () => run(effects.reconnect),
    uiReady: () => run(effects.uiReady),
    uiRendered: () => run(effects.uiRendered),
    subscribe: (
      onEvent: (event: HostEvent) => void,
      onError: (error: BridgeError) => void = () => undefined,
    ) => {
      const consume = (): Effect.Effect<void, never, ApplicationBridgeService> =>
        Effect.flatMap(service, (bridge) =>
          Stream.runForEach(bridge.events, (event) => Effect.sync(() => onEvent(event))),
        ).pipe(Effect.catchAll((error) =>
          Effect.sync(() => onError(error)).pipe(
            Effect.zipRight(Effect.suspend(consume)),
          )));
      const fiber = runtime.runFork(consume());
      return () => {
        runtime.runFork(Fiber.interrupt(fiber));
      };
    },
    run,
    runExit: (program) => run(Effect.exit(program)),
    fork: (program) => runtime.runFork(program),
    await: (fiber) => run(Fiber.await(fiber)),
    interrupt: (fiber) => run(Fiber.interrupt(fiber)),
    dispose: runtime.dispose,
  };
}
