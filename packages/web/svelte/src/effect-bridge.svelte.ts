import type { Effect, Exit } from "effect";
import { SvelteApplicationBridge } from "./bridge.svelte.js";
import { SvelteEffectAction } from "./effect-action.svelte.js";
import type {
  ApplicationBridgeEffects,
  EffectApplicationBridgeController,
  SvelteApplicationBridgeOptions,
} from "./types.js";

interface DisposableEffectAction {
  dispose(): Promise<void>;
}

/** Opt-in Effect surface backed by the Application Bridge's owned runtime. */
export class EffectSvelteApplicationBridge<
  Command,
  Receipt,
  HostEvent,
  Snapshot,
  Failure,
  Requirements,
> extends SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot, Failure> {
  readonly effects: ApplicationBridgeEffects<Command, Receipt, HostEvent, Snapshot, Failure, Requirements>;

  readonly #effectController: EffectApplicationBridgeController<
    Command,
    Receipt,
    HostEvent,
    Snapshot,
    Failure,
    Requirements
  >;
  readonly #actions: DisposableEffectAction[] = [];

  constructor(
    controller: EffectApplicationBridgeController<
      Command,
      Receipt,
      HostEvent,
      Snapshot,
      Failure,
      Requirements
    >,
    options: SvelteApplicationBridgeOptions<HostEvent, Snapshot> = {},
  ) {
    super(controller, options);
    this.#effectController = controller;
    this.effects = controller.effects;
  }

  run<A, E>(program: Effect.Effect<A, E, Requirements>): Promise<A> {
    return this.#effectController.run(program);
  }

  runExit<A, E>(program: Effect.Effect<A, E, Requirements>): Promise<Exit.Exit<A, E>> {
    return this.#effectController.runExit(program);
  }

  /**
   * Creates a latest-wins action whose Fiber is interrupted when superseded or
   * when this application bridge is disposed.
   */
  createAction<Input, Success, Error>(
    program: (
      input: Input,
      effects: ApplicationBridgeEffects<Command, Receipt, HostEvent, Snapshot, Failure, Requirements>,
    ) => Effect.Effect<Success, Error, Requirements>,
  ): SvelteEffectAction<Input, Success, Error, Requirements> {
    if (this.status === "disposed") throw new Error("The Svelte Application Bridge is disposed.");
    let action!: SvelteEffectAction<Input, Success, Error, Requirements>;
    action = new SvelteEffectAction(
      this.#effectController,
      (input) => program(input, this.effects),
      () => {
        const index = this.#actions.indexOf(action);
        if (index >= 0) this.#actions.splice(index, 1);
      },
    );
    this.#actions.push(action);
    return action;
  }

  override async dispose(): Promise<void> {
    if (this.status === "disposed") return;
    const actions = [...this.#actions];
    this.#actions.length = 0;
    await Promise.all(actions.map((action) => action.dispose()));
    await super.dispose();
  }
}

export function createEffectSvelteApplicationBridge<
  Command,
  Receipt,
  HostEvent,
  Snapshot,
  Failure,
  Requirements,
>(
  controller: EffectApplicationBridgeController<
    Command,
    Receipt,
    HostEvent,
    Snapshot,
    Failure,
    Requirements
  >,
  options: SvelteApplicationBridgeOptions<HostEvent, Snapshot> = {},
): EffectSvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot, Failure, Requirements> {
  return new EffectSvelteApplicationBridge(controller, options);
}
