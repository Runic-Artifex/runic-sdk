import {
  Effect,
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
import type { FrameChannel, FrameChannelEvent } from "./transport.js";

export interface ApplicationBridgeOptions {
  readonly commandIdFactory?: () => string;
  readonly maxFrameBytes?: number;
  readonly maxPendingCommands?: number;
}

interface Pending {
  readonly kind: "initialize" | "dispatch" | "cancel" | "signal";
  readonly resolve: (value: unknown) => void;
  readonly reject: (error: BridgeError) => void;
}

const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });

export function CsWebUiApplicationBridgeLive<Command, Receipt, HostEvent, Snapshot>(
  contract: ApplicationContract<Command, Receipt, HostEvent, Snapshot>,
  channel: FrameChannel,
  options: ApplicationBridgeOptions = {},
): Layer.Layer<ApplicationBridgeService> {
  return Layer.scoped(
    ApplicationBridge,
    Effect.gen(function*() {
      const events = yield* PubSub.unbounded<HostEvent>();
      const failures = yield* PubSub.unbounded<BridgeError>();
      const rawFrames = yield* PubSub.unbounded<FrameChannelEvent>();
      const pending = new Map<string, Pending>();
      const seenCommands = new Set<string>();
      const maxFrameBytes = options.maxFrameBytes ?? 262_144;
      const maxPending = options.maxPendingCommands ?? 64;
      const nextCommandId = options.commandIdFactory ?? (() => crypto.randomUUID());
      let sessionId: string | undefined;
      let revision = 0;
      let sequence = 0;
      let stopped = false;
      const effectRuntime = yield* Effect.runtime<never>();
      const runPromise = Runtime.runPromise(effectRuntime);
      const runSync = Runtime.runSync(effectRuntime);

      const failAll = (error: BridgeError): Effect.Effect<void> => Effect.sync(() => {
        for (const item of pending.values()) item.reject(error);
        pending.clear();
      }).pipe(
        Effect.zipRight(PubSub.publish(failures, error)),
        Effect.asVoid,
      );

      const onEnvelope = (envelope: HostEnvelope): Effect.Effect<void> => Effect.gen(function*() {
        if (envelope.protocol !== contract.identity || envelope.version !== contract.version) {
          return yield* failAll(bridgeError("ProtocolVersionMismatch", "The host uses an incompatible Application Bridge contract."));
        }
        if (sessionId !== undefined && envelope.sessionId !== sessionId) {
          return yield* failAll(bridgeError("ProtocolDecodeError", "The host response belongs to a stale session."));
        }
        if (envelope.sequence !== sequence + 1) {
          sequence = 0;
          sessionId = undefined;
          return yield* failAll(bridgeError("ProtocolDecodeError", "A host event sequence gap requires authoritative recovery.", true));
        }
        sequence = envelope.sequence;
        revision = envelope.revision;
        sessionId = envelope.sessionId;

        if (envelope.kind === "event") {
          const event = yield* Schema.decodeUnknown(contract.event, { onExcessProperty: "error" })(envelope.payload).pipe(
            Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host event was invalid.")),
          );
          yield* PubSub.publish(events, event);
          return;
        }
        const commandId = envelope.commandId;
        if (commandId === undefined) {
          return yield* failAll(bridgeError("ProtocolDecodeError", "A correlated host response omitted its command identifier."));
        }
        const item = pending.get(commandId);
        if (item === undefined) return;
        pending.delete(commandId);
        if (envelope.kind === "error") {
          const error = yield* Schema.decodeUnknown(contract.error!, { onExcessProperty: "error" })(envelope.payload).pipe(
            Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host error was invalid.")),
          );
          item.reject(error);
          return;
        }
        const value: unknown = envelope.kind === "snapshot"
          ? yield* Schema.decodeUnknown(contract.snapshot, { onExcessProperty: "error" })(envelope.payload).pipe(
            Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host response payload was invalid.")),
          )
          : yield* Schema.decodeUnknown(contract.receipt, { onExcessProperty: "error" })(envelope.payload).pipe(
            Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host response payload was invalid.")),
          );
        item.resolve(value);
      }).pipe(Effect.catchAll((error) => failAll(error)));

      const onChannelEvent = (event: FrameChannelEvent): Effect.Effect<void> => Effect.gen(function*() {
        if (event._tag === "State") {
          if (event.state !== "connected") {
            yield* failAll(bridgeError("TransportClosed", "The Application Bridge transport closed.", true));
          }
          return;
        }
        if (event.bytes.byteLength > maxFrameBytes) {
          return yield* failAll(bridgeError("ProtocolDecodeError", "The host frame exceeded the configured byte limit."));
        }
        let json: unknown;
        try {
          json = JSON.parse(decoder.decode(event.bytes));
        } catch {
          return yield* failAll(bridgeError("ProtocolDecodeError", "The host frame was not valid UTF-8 JSON."));
        }
        const envelope = yield* Schema.decodeUnknown(HostEnvelopeSchema, { onExcessProperty: "error" })(json).pipe(
          Effect.mapError(() => bridgeError("ProtocolDecodeError", "The host frame was invalid.")),
        );
        yield* onEnvelope(envelope);
      }).pipe(Effect.catchAll((error) => failAll(error)));

      const unsubscribe = channel.subscribe((event) => {
        const copied = event._tag === "Frame"
          ? { _tag: "Frame" as const, bytes: new Uint8Array(event.bytes) }
          : { ...event };
        runSync(PubSub.publish(rawFrames, copied));
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
          let commandId = nextCommandId();
          while (seenCommands.has(commandId)) commandId = crypto.randomUUID();
          seenCommands.add(commandId);
          const item: Pending = {
            kind: kind === "initialize" ? "initialize" : kind === "dispatch" ? "dispatch" : kind === "cancelOperation" ? "cancel" : "signal",
            resolve: (value) => resume(Effect.succeed(value as A)),
            reject: (error) => resume(Effect.fail(error)),
          };
          pending.set(commandId, item);
          const envelope: ClientEnvelope = {
            protocol: contract.identity,
            version: contract.version,
            kind,
            commandId,
            payload,
            ...(sessionId === undefined ? {} : { sessionId }),
            ...(expectedRevision === undefined ? {} : { expectedRevision }),
          };
          runPromise(Schema.encode(ClientEnvelopeSchema)(envelope))
            .then((encoded) => channel.send(encoder.encode(JSON.stringify(encoded))))
            .catch(() => {
              pending.delete(commandId);
              resume(Effect.fail(bridgeError("TransportUnavailable", "The Application Bridge request could not be sent.", true)));
            });
        });

      const initialize = request<Snapshot>("initialize", contract.initialize);
      const service: ApplicationBridgeService<Command, Receipt, HostEvent, Snapshot> = {
        initialize,
        dispatch: (command) => Schema.encode(contract.command)(command).pipe(
          Effect.mapError(() => bridgeError("ProtocolDecodeError", "The command did not satisfy its Effect Schema.")),
          Effect.flatMap((payload) => request<Receipt>("dispatch", payload, revision)),
        ),
        cancel: (operationId) => request<unknown>("cancelOperation", { operationId }, revision).pipe(Effect.asVoid),
        reconnect: Effect.sync(() => {
          sessionId = undefined;
          sequence = 0;
          revision = 0;
        }).pipe(Effect.flatMap(() => request<Snapshot>("initialize", contract.initialize))),
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
) {
  void contract;
  const runtime = createApplicationBridgeRuntime(layer);
  const service = Effect.map(
    ApplicationBridge,
    (value) => value as ApplicationBridgeService<Command, Receipt, HostEvent, Snapshot>,
  );
  const run = <A>(select: (bridge: ApplicationBridgeService<Command, Receipt, HostEvent, Snapshot>) => Effect.Effect<A, BridgeError>) =>
    runtime.runPromise(Effect.flatMap(service, select));

  return {
    initialize: () => run((bridge) => bridge.initialize),
    dispatch: (command: Command) => run((bridge) => bridge.dispatch(command)),
    cancel: (operationId: string) => run((bridge) => bridge.cancel(operationId)),
    reconnect: () => run((bridge) => bridge.reconnect),
    uiReady: () => run((bridge) => bridge.uiReady),
    uiRendered: () => run((bridge) => bridge.uiRendered),
    subscribe: (
      onEvent: (event: HostEvent) => void,
      onError: (error: BridgeError) => void = () => undefined,
    ) => {
      const fiber = runtime.runFork(
        Effect.flatMap(service, (bridge) =>
          Stream.runForEach(bridge.events, (event) => Effect.sync(() => onEvent(event))),
        ).pipe(Effect.catchAll((error) => Effect.sync(() => onError(error)))),
      );
      return () => {
        runtime.runFork(Fiber.interrupt(fiber));
      };
    },
    dispose: runtime.dispose,
  };
}
