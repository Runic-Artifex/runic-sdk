import type { Effect, Exit } from "effect";
import { SvelteApplicationBridge } from "./bridge.svelte.js";
import { SvelteEffectAction } from "./effect-action.svelte.js";
import type {
  ApplicationBridgeController,
  ApplicationBridgeEffects,
  ApplicationBridgeService,
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
> extends SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot> {
  readonly effects: ApplicationBridgeEffects<Command, Receipt, HostEvent, Snapshot>;

  readonly #effectController: ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot>;
  readonly #actions: DisposableEffectAction[] = [];

  constructor(
    controller: ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot>,
    options: SvelteApplicationBridgeOptions<HostEvent, Snapshot> = {},
  ) {
    super(controller, options);
    this.#effectController = controller;
    this.effects = controller.effects;
  }

  run<A, E>(program: Effect.Effect<A, E, ApplicationBridgeService>): Promise<A> {
    return this.#effectController.run(program);
  }

  runExit<A, E>(program: Effect.Effect<A, E, ApplicationBridgeService>): Promise<Exit.Exit<A, E>> {
    return this.#effectController.runExit(program);
  }

  /**
   * Creates a latest-wins action whose Fiber is interrupted when superseded or
   * when this application bridge is disposed.
   */
  createAction<Input, Success, Error>(
    program: (
      input: Input,
      effects: ApplicationBridgeEffects<Command, Receipt, HostEvent, Snapshot>,
    ) => Effect.Effect<Success, Error, ApplicationBridgeService>,
  ): SvelteEffectAction<Input, Success, Error> {
    if (this.status === "disposed") throw new Error("The Svelte Application Bridge is disposed.");
    let action!: SvelteEffectAction<Input, Success, Error>;
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
>(
  controller: ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot>,
  options: SvelteApplicationBridgeOptions<HostEvent, Snapshot> = {},
): EffectSvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot> {
  return new EffectSvelteApplicationBridge(controller, options);
}
