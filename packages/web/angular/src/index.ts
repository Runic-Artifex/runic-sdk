import {
  DestroyRef,
  InjectionToken,
  inject,
  makeEnvironmentProviders,
  signal,
  type EnvironmentProviders,
  type Signal,
  type WritableSignal,
} from "@angular/core";
import {
  Cause,
  Effect,
  Exit,
  Option,
} from "effect";
import {
  type ApplicationBridgeController,
  type ApplicationBridgeService,
  type BridgeError,
  bridgeError,
} from "@runic-artifex/application-bridge";

/** Host-neutral composition for an Angular Application Bridge client. */
export interface ApplicationBridgeAngularConfiguration<Command, Receipt, HostEvent, Snapshot> {
  /** The composition-owned neutral controller. Angular never creates or disposes its runtime. */
  readonly controller: ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot>;
  /** Projects validated host events into the authoritative snapshot, when applicable. */
  readonly snapshotFromEvent?: (event: HostEvent) => Snapshot | undefined;
}

/** Angular-facing facade over the controller's one owned Effect runtime. */
export interface ApplicationBridgeClient<Command, Receipt, HostEvent, Snapshot> {
  readonly snapshot: Signal<Snapshot | undefined>;
  readonly error: Signal<BridgeError | undefined>;
  readonly initialized: Signal<boolean>;
  initialize(): Promise<Snapshot>;
  dispatch(command: Command): Promise<Receipt>;
  cancel(operationId: string): Promise<void>;
  reconnect(): Promise<Snapshot>;
  uiReady(): Promise<void>;
  uiRendered(): Promise<void>;
  subscribe(onEvent: (event: HostEvent) => void): () => void;
}

const configurationToken = new InjectionToken<ApplicationBridgeAngularConfiguration<unknown, unknown, unknown, unknown>>(
  "RunicApplicationBridgeConfiguration",
);
const clientToken = new InjectionToken<ApplicationBridgeClient<unknown, unknown, unknown, unknown>>(
  "RunicApplicationBridgeClient",
);

/**
 * Registers a single bridge client in the application injector. The supplied
 * controller can target Runic Desktop, CS-WebUI compatibility, a hosted
 * WebSocket, or a mock without changing the Angular package boundary.
 */
export function provideApplicationBridge<Command, Receipt, HostEvent, Snapshot>(
  configuration: ApplicationBridgeAngularConfiguration<Command, Receipt, HostEvent, Snapshot>,
): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: configurationToken, useValue: configuration as ApplicationBridgeAngularConfiguration<unknown, unknown, unknown, unknown> },
    {
      provide: clientToken,
      useFactory: () => createClient(
        inject(configurationToken),
        inject(DestroyRef),
      ),
    },
  ]);
}

/** Injects the typed facade registered by {@link provideApplicationBridge}. */
export function injectApplicationBridge<Command, Receipt, HostEvent, Snapshot>(): ApplicationBridgeClient<
  Command,
  Receipt,
  HostEvent,
  Snapshot
> {
  return inject(clientToken) as ApplicationBridgeClient<Command, Receipt, HostEvent, Snapshot>;
}

function createClient<Command, Receipt, HostEvent, Snapshot>(
  configuration: ApplicationBridgeAngularConfiguration<Command, Receipt, HostEvent, Snapshot>,
  destroyRef: DestroyRef,
): ApplicationBridgeClient<Command, Receipt, HostEvent, Snapshot> {
  const controller = configuration.controller;
  const snapshot = signal<Snapshot | undefined>(undefined);
  const error = signal<BridgeError | undefined>(undefined);
  const initialized = signal(false);
  const unsubscribe = controller.subscribe(
    (event) => {
      const projected = configuration.snapshotFromEvent?.(event);
      if (projected !== undefined) snapshot.set(projected);
    },
    (failure) => error.set(failure),
  );
  destroyRef.onDestroy(() => {
    unsubscribe();
  });

  return {
    snapshot: snapshot.asReadonly(),
    error: error.asReadonly(),
    initialized: initialized.asReadonly(),
    initialize: () => run(controller, controller.effects.initialize, snapshot, error, initialized),
    dispatch: (command) => run(controller, controller.effects.dispatch(command), undefined, error),
    cancel: (operationId) => run(controller, controller.effects.cancel(operationId), undefined, error),
    reconnect: () => run(controller, controller.effects.reconnect, snapshot, error, initialized),
    uiReady: () => run(controller, controller.effects.uiReady, undefined, error),
    uiRendered: () => run(controller, controller.effects.uiRendered, undefined, error),
    subscribe: (onEvent) => controller.subscribe(onEvent, (failure) => error.set(failure)),
  };
}

async function run<Command, Receipt, HostEvent, Snapshot, Value>(
  controller: ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot>,
  operation: Effect.Effect<Value, BridgeError, ApplicationBridgeService>,
  snapshot: WritableSignal<Value | undefined> | undefined,
  error: WritableSignal<BridgeError | undefined>,
  initialized?: WritableSignal<boolean>,
): Promise<Value> {
  error.set(undefined);
  try {
    const outcome = await controller.runExit(operation);
    if (Exit.isFailure(outcome)) {
      const failure = Cause.failureOption(outcome.cause);
      throw Option.isSome(failure)
        ? failure.value
        : bridgeError("OperationFailed", "The Application Bridge operation was interrupted.");
    }
    const value = outcome.value;
    snapshot?.set(value);
    initialized?.set(true);
    return value;
  } catch (failure) {
    error.set(failure as BridgeError);
    throw failure;
  }
}
