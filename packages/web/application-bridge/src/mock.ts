import { Effect, Layer, PubSub, Stream } from "effect";
import { bridgeError, type BridgeError } from "./errors.js";
import { ApplicationBridge, type ApplicationBridgeService } from "./service.js";

export interface MockApplicationBridgeFixture<Command, Receipt, HostEvent, Snapshot, Failure = never> {
  readonly initialize: () => Effect.Effect<Snapshot, Failure>;
  readonly dispatch: (command: Command, publish: (event: HostEvent) => Effect.Effect<void>) => Effect.Effect<Receipt, Failure>;
  readonly cancel?: (operationId: string) => Effect.Effect<void, Failure>;
}

export function MockApplicationBridge<Command, Receipt, HostEvent, Snapshot, Failure = never>(
  fixture: MockApplicationBridgeFixture<Command, Receipt, HostEvent, Snapshot, Failure>,
): Layer.Layer<ApplicationBridgeService<Command, Receipt, HostEvent, Snapshot, BridgeError | Failure>> {
  return Layer.scoped(
    ApplicationBridge,
    Effect.gen(function*() {
      const events = yield* PubSub.unbounded<HostEvent>();
      yield* Effect.addFinalizer(() => PubSub.shutdown(events));
      const service: ApplicationBridgeService<Command, Receipt, HostEvent, Snapshot, BridgeError | Failure> = {
        initialize: fixture.initialize(),
        dispatch: (command) => fixture.dispatch(command, (event) => PubSub.publish(events, event).pipe(Effect.asVoid)),
        cancel: fixture.cancel ?? (() => Effect.void),
        reconnect: fixture.initialize(),
        uiReady: Effect.void,
        uiRendered: Effect.void,
        events: Stream.fromPubSub(events),
      };
      return service as ApplicationBridgeService;
    }),
  ) as unknown as Layer.Layer<ApplicationBridgeService<Command, Receipt, HostEvent, Snapshot, BridgeError | Failure>>;
}

export interface FaultInjectionPlan {
  readonly failInitialize?: boolean;
  readonly rejectCommandTags?: ReadonlySet<string>;
  readonly interruptAfterCommands?: number;
}

export function TestApplicationBridge(
  base: Layer.Layer<ApplicationBridgeService>,
  plan: FaultInjectionPlan,
): Layer.Layer<ApplicationBridgeService> {
  return Layer.effect(
    ApplicationBridge,
    Effect.gen(function*() {
      const service = yield* ApplicationBridge;
      let commands = 0;
      return {
        ...service,
        initialize: plan.failInitialize === true
          ? Effect.fail(bridgeError("TransportUnavailable", "Injected initialization failure.", true))
          : service.initialize,
        dispatch: (command: unknown) => {
          commands++;
          const tag = typeof command === "object" && command !== null && "_tag" in command
            ? String(command._tag)
            : "";
          if (plan.rejectCommandTags?.has(tag) === true) {
            return Effect.fail(bridgeError("CommandRejected", "Injected command rejection."));
          }
          if (plan.interruptAfterCommands !== undefined && commands > plan.interruptAfterCommands) {
            return Effect.fail(bridgeError("TransportClosed", "Injected transport interruption.", true));
          }
          return service.dispatch(command);
        },
      } satisfies ApplicationBridgeService;
    }),
  ).pipe(Layer.provide(base));
}
