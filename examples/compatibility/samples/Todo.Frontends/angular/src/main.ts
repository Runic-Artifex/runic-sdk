import { provideZonelessChangeDetection } from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import {
  provideAngularMvvmApplication,
  startAngularMvvmApplication,
} from "@webuitoolkit/mvvm-angular";

import {
  AdvancedTodoContract,
  demoFromDocument,
  SimpleTodoContract,
} from "../../shared/contracts";
import { exposeTodoReconnect, reportStartupFailure } from "../../shared/runtime";
import { AdvancedTodoComponent } from "./advanced/advanced-todo.component";
import { SimpleTodoComponent } from "./simple/simple-todo.component";
import {
  provideAdvancedTodoContract,
  provideSimpleTodoContract,
} from "./todo-bindings.g";

try {
  if (demoFromDocument() === "simple") {
    const native = await startAngularMvvmApplication({
      contract: SimpleTodoContract,
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
  } else {
    const native = await startAngularMvvmApplication({
      contract: AdvancedTodoContract,
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
} catch (error) {
  reportStartupFailure(error);
}
