import { assertInInjectionContext, DestroyRef, inject, Injector, signal, untracked, type Signal } from "@angular/core";
import {
  createCommandHandles,
  type BridgeOutcomeFailure,
  type CommandArguments,
  type CommandName,
  type CommandResult,
} from "@runic-artifex/views";
import type { CommandHandle, InjectCommandOptions } from "./inject-command.js";

/** The View whose commands a component runs: an {@link injectView} handle, or any client signal. */
export interface CommandsView<TClient extends object> {
  readonly client: Signal<TClient | undefined>;
}

/** One {@link CommandHandle} per command of a generated client, such as `commands.refresh` and `commands.archive`. */
export type CommandHandles<TClient> = {
  readonly [TName in CommandName<TClient>]: CommandHandle<
    CommandArguments<TClient, TName>, CommandResult<TClient, TName>, BridgeOutcomeFailure<CommandResult<TClient, TName>>>;
};

/**
 * The commands of a View as {@link CommandHandle}s, for the lifetime of the injection context, so a template calls a
 * command without an {@link injectCommand} per command:
 *
 * ```ts
 * readonly orders = injectView(this.page);
 * readonly commands = injectCommands(this.orders);
 * // <button (click)="commands.refresh.run()" [disabled]="commands.refresh.pending()">Refresh</button>
 * // <button (click)="commands.archive.run(row.id)">Archive</button>
 * ```
 *
 * The names and arguments are typed from the generated client's commands (`{View}CommandName`); setters, `canX(arg)`,
 * `startX`, `recoverX`, `upload` and `dispose` are not offered. A handle is made when the component first reads its
 * name and kept until the context is destroyed. Each has its own `pending`, `error` and `failure`, as an
 * `injectCommand(() => this.orders.client()?.refresh())` would. A run calls the client connected when it starts, and
 * before the client connected it resolves to `undefined`. Like `injectCommand`'s, `run()` never rejects: a failed run
 * ends in `error`.
 */
export function injectCommands<TClient extends object>(view: CommandsView<TClient>, options: InjectCommandOptions = {}): CommandHandles<TClient> {
  if (!options.injector) assertInInjectionContext(injectCommands);
  const injector = options.injector ?? inject(Injector);
  const commands = createCommandHandles({
    client: () => untracked(view.client),
    handle: controller => {
      const pending = signal(controller.current.pending);
      const error = signal<unknown>(controller.current.error);
      const failure = signal<unknown>(controller.current.failure);
      controller.subscribe(() => {
        pending.set(controller.current.pending);
        error.set(controller.current.error);
        failure.set(controller.current.failure);
      });
      return { run: controller.run, pending: pending.asReadonly(), error: error.asReadonly(), failure: failure.asReadonly(), reset: controller.reset };
    },
  });
  injector.get(DestroyRef).onDestroy(() => commands.dispose());
  return commands.handles as unknown as CommandHandles<TClient>;
}
