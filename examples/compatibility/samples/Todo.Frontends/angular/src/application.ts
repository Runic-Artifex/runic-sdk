import { provideZonelessChangeDetection } from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import type { FrameChannel } from "@runic-artifex/mvvm";
import {
  provideAngularMvvmApplication,
  startAngularMvvmApplication,
} from "@runic-artifex/mvvm-angular";

import {
  AdvancedTodoContract,
  demoFromDocument,
  SimpleTodoContract,
  type TodoDemo,
} from "../../shared/contracts";
import { exposeTodoReconnect } from "../../shared/runtime";
import { AdvancedTodoComponent } from "./advanced/advanced-todo.component";
import { SimpleTodoComponent } from "./simple/simple-todo.component";
import {
  provideAdvancedTodoContract,
  provideSimpleTodoContract,
} from "./todo-bindings.g";

export type TodoChannelFactory = (
  demo: TodoDemo,
) => FrameChannel | Promise<FrameChannel>;

export async function bootstrapTodoApplication(
  channelFactory?: TodoChannelFactory,
): Promise<void> {
  const demo = demoFromDocument();
  const ownerOptions = channelFactory === undefined
    ? {}
    : { channelFactory: () => channelFactory(demo) };

  if (demo === "simple") {
    const native = await startAngularMvvmApplication({
      contract: SimpleTodoContract,
      ...ownerOptions,
    });
    const angular = await bootstrapApplication(SimpleTodoComponent, {
      providers: [
        provideZonelessChangeDetection(),
        ...provideAngularMvvmApplication(native),
        ...provideSimpleTodoContract(native.store, native.contract),
      ],
    });
    native.addCleanup(() => angular.destroy());
    exposeTodoReconnect(native);
    return;
  }

  const native = await startAngularMvvmApplication({
    contract: AdvancedTodoContract,
    ...ownerOptions,
  });
  const angular = await bootstrapApplication(AdvancedTodoComponent, {
    providers: [
      provideZonelessChangeDetection(),
      ...provideAngularMvvmApplication(native),
      ...provideAdvancedTodoContract(native.store, native.contract),
    ],
  });
  native.addCleanup(() => angular.destroy());
  exposeTodoReconnect(native);
}
