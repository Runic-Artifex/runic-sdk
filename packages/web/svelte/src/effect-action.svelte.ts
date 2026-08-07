import { Cause, Effect, Exit, Option, type Fiber } from "effect";
import type { EffectActionStatus, EffectRunner } from "./types.js";

/**
 * Projects one latest-wins Effect workflow into Svelte-native reactive state.
 * The supplied runner owns the Fiber; this class never creates an Effect runtime.
 */
export class SvelteEffectAction<Input, Success, Failure, Requirements> {
  status = $state<EffectActionStatus>("idle");
  value = $state.raw<Success | undefined>(undefined);
  error = $state.raw<Failure | undefined>(undefined);
  cause = $state.raw<Cause.Cause<Failure> | undefined>(undefined);
  exit = $state.raw<Exit.Exit<Success, Failure> | undefined>(undefined);

  readonly #runner: EffectRunner<Requirements>;
  readonly #program: (input: Input) => Effect.Effect<Success, Failure, Requirements>;
  readonly #onDispose: (() => void) | undefined;
  #fiber: Fiber.RuntimeFiber<Success, Failure> | undefined;
  #generation = 0;
  #disposed = false;

  constructor(
    runner: EffectRunner<Requirements>,
    program: (input: Input) => Effect.Effect<Success, Failure, Requirements>,
    onDispose?: () => void,
  ) {
    this.#runner = runner;
    this.#program = program;
    this.#onDispose = onDispose;
  }

  get running(): boolean {
    return this.status === "running";
  }

  /** Runs the workflow, interrupting an older invocation owned by this action. */
  async run(input: Input): Promise<Exit.Exit<Success, Failure>> {
    if (this.#disposed) throw new Error("The Svelte Effect action is disposed.");
    const generation = ++this.#generation;
    const previous = this.#fiber;
    if (previous !== undefined) await this.#runner.interrupt(previous);
    if (this.#disposed || generation !== this.#generation) {
      throw new Error("The Svelte Effect action was disposed before it could start.");
    }

    this.status = "running";
    this.value = undefined;
    this.error = undefined;
    this.cause = undefined;
    this.exit = undefined;

    const fiber = this.#runner.fork(Effect.suspend(() => this.#program(input)));
    this.#fiber = fiber;
    const exit = await this.#runner.await(fiber);
    if (generation === this.#generation && !this.#disposed) {
      this.#fiber = undefined;
      this.#apply(exit);
    }
    return exit;
  }

  /** Interrupts the current frontend workflow without implying backend cancellation. */
  async interrupt(): Promise<Exit.Exit<Success, Failure> | undefined> {
    if (this.#disposed) return undefined;
    const fiber = this.#fiber;
    if (fiber === undefined) return this.exit;
    ++this.#generation;
    this.#fiber = undefined;
    const exit = await this.#runner.interrupt(fiber);
    if (!this.#disposed) this.#apply(exit);
    return exit;
  }

  reset(): void {
    if (this.#disposed) throw new Error("The Svelte Effect action is disposed.");
    if (this.#fiber !== undefined) throw new Error("A running Svelte Effect action cannot be reset.");
    this.status = "idle";
    this.value = undefined;
    this.error = undefined;
    this.cause = undefined;
    this.exit = undefined;
  }

  async dispose(): Promise<void> {
    if (this.#disposed) return;
    this.#disposed = true;
    ++this.#generation;
    const fiber = this.#fiber;
    this.#fiber = undefined;
    if (fiber !== undefined) await this.#runner.interrupt(fiber);
    this.status = "disposed";
    this.#onDispose?.();
  }

  #apply(exit: Exit.Exit<Success, Failure>): void {
    this.exit = exit;
    if (Exit.isSuccess(exit)) {
      this.value = exit.value;
      this.status = "success";
      return;
    }
    this.cause = exit.cause;
    this.error = Option.getOrUndefined(Cause.failureOption(exit.cause));
    this.status = Cause.isInterruptedOnly(exit.cause) ? "interrupted" : "failure";
  }
}
